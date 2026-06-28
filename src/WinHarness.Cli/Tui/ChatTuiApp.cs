using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WinHarness.Cli.Chat;
using WinHarness.Cli.Rendering;
using WinHarness.Configuration;
using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Sessions;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Tui;

internal sealed class ChatTuiApp
{
    private readonly IApplication _app;
    private readonly IServiceProvider _services;
    private readonly WinHarnessOptions _options;
    private readonly ChatSession _session;
    private readonly SlashCommandContext _slashContext;
    private readonly List<TranscriptMessage> _messages = [];
    private readonly ObservableCollection<TranscriptRow> _transcriptRows = [];
    private readonly CancellationTokenSource _shutdownCts;

    private Label _status = null!;
    private ListView _transcript = null!;
    private Label _toolStatus = null!;
    private TextField _input = null!;
    private bool _turnRunning;
    private int _activeAssistantRowIndex = -1;
    private Task _activeTurn = Task.CompletedTask;

    private ChatTuiApp(
        IApplication app,
        IServiceProvider services,
        ChatSession session,
        CancellationTokenSource shutdownCts)
    {
        _app = app;
        _services = services;
        _options = services.GetRequiredService<WinHarnessOptions>();
        _session = session;
        _shutdownCts = shutdownCts;
        _slashContext = new SlashCommandContext(
            services,
            services.GetRequiredService<SessionManagerFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            shutdownCts.Token,
            TreePickerAsync: PickTreeAsync);
    }

    public static async ValueTask RunAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        ChatSessionBootstrapRequest bootstrapRequest,
        CancellationToken cancellationToken)
    {
        SessionManagerFactory factory = services.GetRequiredService<SessionManagerFactory>();
        IContextFileLoader contextFileLoader = services.GetRequiredService<IContextFileLoader>();
        ISessionManager sessionManager = await ChatSessionBootstrap.ResolveAsync(
            factory,
            bootstrapRequest,
            cancellationToken);
        ChatSession session = ChatSessionBootstrap.CreateChatSession(
            sessionManager,
            contextFileLoader,
            providerId,
            modelId,
            renderMarkdown);

        using CancellationTokenSource shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IApplication app = Application.Create();
        app.Init();
        ChatTuiApp chat = new(app, services, session, shutdownCts);
        using Window window = chat.BuildWindow();
        using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            IApplication application = (IApplication)state!;
            application.Invoke(() => application.RequestStop());
        }, app);

        chat.InitializeTranscript();
        chat.AppendSystem("Type /help for commands · Ctrl+Q quit · Enter send");
        bool initialFocusSet = false;
        app.Iteration += (_, _) =>
        {
            if (!initialFocusSet)
            {
                initialFocusSet = true;
                chat.FocusInput();
            }
        };
        try
        {
            app.Run(window);
        }
        finally
        {
            await chat.ShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task ShutdownAsync()
    {
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _activeTurn.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RequestQuit()
    {
        _shutdownCts.Cancel();
        _app.RequestStop();
    }

    private Window BuildWindow()
    {
        Window window = new()
        {
            Title = "WinHarness chat",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _status = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill()! - 2,
            Height = 1
        };
        _status.SetScheme(new Scheme
        {
            Normal = TuiTheme.StatusText
        });

        FrameView transcriptFrame = new()
        {
            Title = " Conversation ",
            X = 0,
            Y = 1,
            Width = Dim.Percent(70),
            Height = Dim.Fill()! - 4,
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        transcriptFrame.SetScheme(new Scheme
        {
            Normal = TuiTheme.Border,
            Focus = TuiTheme.Border
        });

        _transcript = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            Source = new TranscriptDataSource(_transcriptRows)
        };
        transcriptFrame.Add(_transcript);

        FrameView toolsFrame = new()
        {
            Title = "Tools",
            X = Pos.Right(transcriptFrame),
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()! - 4,
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        toolsFrame.SetScheme(new Scheme
        {
            Normal = TuiTheme.Border,
            Focus = TuiTheme.Border
        });

        _toolStatus = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill()! - 2,
            Height = Dim.Fill(),
            Text = "idle"
        };
        _toolStatus.SetScheme(new Scheme
        {
            Normal = TuiTheme.ToolText
        });
        toolsFrame.Add(_toolStatus);

        Label inputLabel = new()
        {
            Text = "›",
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = 2,
            Height = 1,
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        inputLabel.SetScheme(new Scheme
        {
            Normal = TuiTheme.AccentText
        });

        _input = new TextField
        {
            X = 2,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill()! - 2,
            Height = 1,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        _input.SetScheme(new Scheme
        {
            Normal = TuiTheme.InputText,
            Focus = TuiTheme.InputFocus,
            Editable = TuiTheme.InputText
        });
        _input.Accepting += (_, args) =>
        {
            args.Handled = true;
            _ = SubmitCurrentInputAsync();
        };

        Bar statusBar = new()
        {
            Orientation = Orientation.Horizontal,
            Y = Pos.AnchorEnd(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        statusBar.Add(new Shortcut
        {
            Title = "_Reload",
            HelpText = "Reload transcript from session",
            Key = Key.L.WithCtrl,
            Action = ReloadTranscriptFromSession
        });
        statusBar.Add(new Shortcut
        {
            Title = "_Quit",
            HelpText = "Quit",
            Key = Key.Q.WithCtrl,
            Action = RequestQuit
        });

        window.Add(_status, transcriptFrame, toolsFrame, inputLabel, _input, statusBar);
        UpdateStatus();
        return window;
    }

    private void FocusInput()
    {
        if (!_input.Enabled)
        {
            return;
        }

        _input.SetFocus();
        _input.SetNeedsDraw();
    }

    private void InitializeTranscript()
    {
        _messages.Clear();
        PopulateTranscriptMessagesFromSession();
        RebuildWrappedTranscript(scrollToEnd: false);
    }

    private void LoadTranscriptFromSession()
    {
        InvokeUi(() =>
        {
            _messages.Clear();
            PopulateTranscriptMessagesFromSession();
            RebuildWrappedTranscript(scrollToEnd: false);
        });
    }

    private void ReloadTranscriptFromSession()
    {
        if (_turnRunning)
        {
            AppendSystem("Wait for the current turn to finish before reloading.");
            return;
        }

        InvokeUi(() =>
        {
            _messages.Clear();
            PopulateTranscriptMessagesFromSession();
            RebuildWrappedTranscript(scrollToEnd: false);
        });
    }

    private void PopulateTranscriptMessagesFromSession()
    {
        foreach (ConversationMessage message in _session.Conversation.Messages)
        {
            TranscriptRole role = message.Role switch
            {
                ConversationRole.User => TranscriptRole.User,
                ConversationRole.Assistant => TranscriptRole.Assistant,
                _ => TranscriptRole.System
            };
            _messages.Add(new TranscriptMessage(role, message.Text));
        }
    }

    private int GetTranscriptWrapWidth()
    {
        int width = _transcript.Viewport.Width;
        if (width <= 1)
        {
            width = _app.Screen.Size.Width - 4;
        }

        return Math.Max(24, width - 1);
    }

    private void RebuildWrappedTranscript(bool scrollToEnd)
    {
        int width = GetTranscriptWrapWidth();
        _transcriptRows.Clear();
        for (int index = 0; index < _messages.Count; index++)
        {
            AppendMessageToTranscript(_messages[index], width, addSpacer: index > 0);
        }

        if (scrollToEnd && _transcriptRows.Count > 0)
        {
            _transcript.MoveEnd(false);
            _transcript.SelectedItem = null;
        }

        _transcript.SetNeedsDraw();
    }

    private void AppendMessageToTranscript(TranscriptMessage message, int width, bool addSpacer)
    {
        if (addSpacer)
        {
            _transcriptRows.Add(new TranscriptRow(message.Role, string.Empty, TranscriptRowKind.Spacer));
        }

        string roleLabel = message.Role switch
        {
            TranscriptRole.User => "you",
            TranscriptRole.Assistant => "assistant",
            TranscriptRole.System => "system",
            _ => "system"
        };
        _transcriptRows.Add(new TranscriptRow(message.Role, roleLabel, TranscriptRowKind.RoleLabel));

        if (_session.RenderMarkdown && message.Role == TranscriptRole.Assistant)
        {
            AppendMarkdownContent(message.Role, message.Text, width);
        }
        else
        {
            AppendPlainContent(message.Role, message.Text, width);
        }
    }

    private void AppendPlainContent(TranscriptRole role, string text, int width)
    {
        const string indent = "  ";
        int contentWidth = Math.Max(1, width - indent.Length);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        bool any = false;
        foreach (string chunk in TranscriptWrap.WordWrap(normalized, contentWidth))
        {
            any = true;
            _transcriptRows.Add(new TranscriptRow(role, indent + chunk));
        }

        if (!any)
        {
            _transcriptRows.Add(new TranscriptRow(role, indent));
        }
    }

    private void AppendMarkdownContent(TranscriptRole role, string markdown, int width)
    {
        const string indent = "  ";
        int contentWidth = Math.Max(1, width - indent.Length);

        foreach (MarkdownDisplayLine displayLine in MarkdownTuiFormatter.ParseLines(markdown))
        {
            foreach ((string chunk, MarkdownBlockStyle blockStyle, IReadOnlyList<MarkdownRun> runs) in MarkdownTuiFormatter.WordWrap(
                         displayLine,
                         contentWidth))
            {
                string rowText = indent + chunk;
                IReadOnlyList<MarkdownRun>? shiftedRuns = runs.Count == 0
                    ? null
                    : ShiftRuns(runs, indent.Length);
                _transcriptRows.Add(new TranscriptRow(role, rowText, TranscriptRowKind.Content, blockStyle, shiftedRuns));
            }
        }
    }

    private static IReadOnlyList<MarkdownRun> ShiftRuns(IReadOnlyList<MarkdownRun> runs, int offset)
    {
        if (offset == 0)
        {
            return runs;
        }

        MarkdownRun[] shifted = new MarkdownRun[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            MarkdownRun run = runs[i];
            shifted[i] = run with { Start = run.Start + offset };
        }

        return shifted;
    }

    private async Task SubmitCurrentInputAsync()
    {
        if (_turnRunning)
        {
            AppendSystem("A turn is already running.");
            return;
        }

        string input = _input.Text?.ToString()?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return;
        }

        _input.Text = string.Empty;

        if (input.StartsWith('/'))
        {
            SlashCommandResult result = await SlashCommandProcessor.ExecuteAsync(
                _options,
                _session,
                input,
                _slashContext).ConfigureAwait(false);

            if (ShouldReloadTranscriptAfterSlashCommand(input))
            {
                ReloadTranscriptFromSession();
            }

            foreach (string message in result.Messages)
            {
                AppendSystem(message);
            }

            if (IsMarkdownToggle(input))
            {
                InvokeUi(() => RebuildWrappedTranscript(scrollToEnd: true));
            }

            UpdateStatus();
            if (result.ShouldExit)
            {
                RequestQuit();
            }

            return;
        }

        _activeTurn = RunTurnAsync(input);
        await _activeTurn.ConfigureAwait(false);
    }

    private async Task RunTurnAsync(string prompt)
    {
        _turnRunning = true;
        SetInputEnabled(false);
        AppendUser(prompt);
        WinHarness.Conversation.Conversation runConversation = _session.CreateRunConversation(prompt);
        _activeAssistantRowIndex = -1;

        try
        {
            IAgentRuntime runtime = _services.GetRequiredService<IAgentRuntime>();
            await foreach (AgentEvent agentEvent in runtime.RunAsync(
                               new AgentRunRequest(
                                   _session.ProviderId,
                                   _session.ModelId,
                                   runConversation,
                                   _session.WorkspaceRoot,
                                   _session.ProjectContext),
                               _shutdownCts.Token).ConfigureAwait(false))
            {
                switch (agentEvent.Kind)
                {
                    case AgentEventKind.ToolActivity:
                        UpdateToolStatus(agentEvent.Message);
                        break;
                    case AgentEventKind.Failed:
                        UpdateToolStatus("idle");
                        AppendSystem("Error: " + agentEvent.Message);
                        break;
                    case AgentEventKind.Completed:
                        if (agentEvent.TurnArtifacts is not null)
                        {
                            await _session.AppendTurnAsync(agentEvent.TurnArtifacts, _shutdownCts.Token)
                                .ConfigureAwait(false);
                        }

                        break;
                    case AgentEventKind.AssistantDelta:
                        AppendAssistantDelta(agentEvent.Message);
                        break;
                    default:
                        break;
                }
            }

            UpdateToolStatus("idle");
        }
        finally
        {
            _activeAssistantRowIndex = -1;
            _turnRunning = false;
            SetInputEnabled(true);
        }
    }

    private async ValueTask<IReadOnlyList<string>> PickTreeAsync(ISessionManager sessionManager)
    {
        TaskCompletionSource<IReadOnlyList<string>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Invoke(() =>
        {
            try
            {
                IReadOnlyList<string> messages = SessionTreeDialog.Show(
                    _app,
                    sessionManager,
                    entryId =>
                    {
                        sessionManager.BranchTo(entryId);
                        _session.SyncConversationFromSession();
                    });
                completion.SetResult(messages);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }

    private static bool ShouldReloadTranscriptAfterSlashCommand(string input)
    {
        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?.ToLowerInvariant() ?? string.Empty;
        return command is "/tree" or "/new" or "/resume" or "/fork" or "/compact";
    }

    private static bool IsMarkdownToggle(string input)
    {
        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?.ToLowerInvariant() ?? string.Empty;
        return command is "/markdown";
    }

    private void AppendSystem(string message)
    {
        InvokeUi(() =>
        {
            _messages.Add(new TranscriptMessage(TranscriptRole.System, message));
            RebuildWrappedTranscript(scrollToEnd: true);
        });
    }

    private void AppendUser(string message)
    {
        InvokeUi(() =>
        {
            _messages.Add(new TranscriptMessage(TranscriptRole.User, message));
            RebuildWrappedTranscript(scrollToEnd: true);
        });
    }

    private void AppendAssistantDelta(string delta)
    {
        InvokeUi(() =>
        {
            if (_activeAssistantRowIndex < 0)
            {
                _messages.Add(new TranscriptMessage(TranscriptRole.Assistant, string.Empty));
                _activeAssistantRowIndex = _messages.Count - 1;
            }

            TranscriptMessage row = _messages[_activeAssistantRowIndex];
            string normalized = delta.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            _messages[_activeAssistantRowIndex] = row with { Text = row.Text + normalized };
            RebuildWrappedTranscript(scrollToEnd: true);
        });
    }

    private void UpdateToolStatus(string message)
    {
        InvokeUi(() =>
        {
            _toolStatus.Text = message;
            _toolStatus.SetNeedsDraw();
        });
    }

    private void UpdateStatus()
    {
        InvokeUi(() =>
        {
            _status.Text = TruncateStatusLine(BuildStatusText());
        });
    }

    private string TruncateStatusLine(string text)
    {
        int maxWidth = Math.Max(24, _app.Screen.Size.Width - 4);
        if (text.Length <= maxWidth)
        {
            return text;
        }

        return maxWidth <= 1 ? text[..maxWidth] : string.Concat(text.AsSpan(0, maxWidth - 1), "…");
    }

    private string BuildStatusText()
    {
        string skill = _session.SelectedSkill is null ? "none" : _session.SelectedSkill.Name;
        List<string> parts =
        [
            $"provider {_session.ProviderId}",
            $"model {_session.ModelId}",
            $"markdown {(_session.RenderMarkdown ? "on" : "off")}",
            $"skill {skill}",
            BuildSessionStatusLabel(),
        ];

        string? contextLine = ContextBannerFormatter.Format(_session.ProjectContext);
        if (contextLine is not null)
        {
            parts.Add(contextLine);
        }

        return string.Join(" · ", parts);
    }

    private string BuildSessionStatusLabel()
    {
        if (_session.IsEphemeral)
        {
            return "session ephemeral";
        }

        string shortId = FormatShortSessionId(_session.SessionManager.Header.Id);
        string? displayName = _session.SessionManager.DisplayName;
        return displayName is null
            ? $"session persisted {shortId}"
            : $"session persisted {shortId} · {displayName}";
    }

    private static string FormatShortSessionId(string headerId)
    {
        string normalized = headerId.Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    private void SetInputEnabled(bool enabled)
    {
        InvokeUi(() =>
        {
            _input.Enabled = enabled;
            _input.Text = enabled ? string.Empty : "running...";
            _status.Text = TruncateStatusLine(BuildStatusText());
            if (enabled)
            {
                FocusInput();
            }
        });
    }

    private void InvokeUi(Action action)
    {
        _app.Invoke(action);
    }

    /// <summary>A logical transcript message before wrapping for display.</summary>
    private sealed record TranscriptMessage(TranscriptRole Role, string Text);

    /// <summary>A wrapped display line tagged with the role that produced it.</summary>
    private sealed record TranscriptRow(
        TranscriptRole Role,
        string Text,
        TranscriptRowKind Kind = TranscriptRowKind.Content,
        MarkdownBlockStyle BlockStyle = MarkdownBlockStyle.None,
        IReadOnlyList<MarkdownRun>? Runs = null);

    /// <summary>
    /// Custom <see cref="IListDataSource"/> that renders each transcript row with pi-inspired
    /// message blocks, role labels, and markdown styling.
    /// </summary>
    private sealed class TranscriptDataSource : IListDataSource, IDisposable
    {
        private readonly ObservableCollection<TranscriptRow> _rows;
        private bool _disposed;

        public TranscriptDataSource(ObservableCollection<TranscriptRow> rows)
        {
            _rows = rows;
        }

        public int Count => _rows.Count;

        public int MaxItemLength => _rows.Count == 0 ? 0 : _rows.Max(static r => r.Text.Length);

        public bool SuspendCollectionChangedEvent { get; set; }

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => _rows.CollectionChanged += value;
            remove => _rows.CollectionChanged -= value;
        }

        public bool IsMarked(int item) => false;

        public void SetMark(int item, bool value)
        {
        }

        public IList ToList() => _rows.ToList();

        public void Render(ListView container, bool selected, int item, int col, int line, int width, int start)
        {
            if ((uint)item >= (uint)_rows.Count)
            {
                return;
            }

            TranscriptRow row = _rows[item];
            container.Move(col, line);

            Attribute baseAttr = row.Kind switch
            {
                TranscriptRowKind.Spacer => TuiTheme.DefaultText,
                TranscriptRowKind.RoleLabel => TuiTheme.RoleLabelFor(row.Role),
                _ => TuiTheme.ContentFor(row.Role)
            };

            if (row.Kind == TranscriptRowKind.Content && row.BlockStyle != MarkdownBlockStyle.None)
            {
                baseAttr = MarkdownTuiStyles.ForBlock(row.BlockStyle, baseAttr);
            }

            string text = row.Text;
            if (text.Length > width)
            {
                text = text[..width];
            }

            if (row.Kind == TranscriptRowKind.Content && row.Runs is { Count: > 0 })
            {
                RenderStyledText(container, text, row.Runs, baseAttr);
            }
            else
            {
                container.SetAttribute(baseAttr);
                container.AddStr(text);
            }

            int remaining = width - text.Length;
            if (remaining > 0)
            {
                container.SetAttribute(baseAttr);
                container.AddStr(new string(' ', remaining));
            }
        }

        private static void RenderStyledText(
            ListView container,
            string text,
            IReadOnlyList<MarkdownRun> runs,
            Attribute baseAttr)
        {
            int pos = 0;
            foreach (MarkdownRun run in runs.OrderBy(static r => r.Start))
            {
                if (run.Start > text.Length)
                {
                    break;
                }

                int runStart = run.Start;
                int runLength = Math.Min(run.Length, text.Length - runStart);
                if (runLength <= 0)
                {
                    continue;
                }

                if (runStart > pos)
                {
                    container.SetAttribute(baseAttr);
                    container.AddStr(text[pos..runStart]);
                }

                container.SetAttribute(MarkdownTuiStyles.ForEmphasis(run.Emphasis, baseAttr));
                container.AddStr(text[runStart..(runStart + runLength)]);
                pos = runStart + runLength;
            }

            if (pos < text.Length)
            {
                container.SetAttribute(baseAttr);
                container.AddStr(text[pos..]);
            }
        }

        public bool RenderMark(ListView container, int item, int col, int line, bool marked, bool selected)
        {
            // No mark column is rendered; marks are unused in the chat transcript.
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
    }

    private static class TranscriptWrap
    {
        public static IEnumerable<string> WordWrap(string text, int width)
        {
            if (text.Length == 0)
            {
                yield return string.Empty;
                yield break;
            }

            string remaining = text;
            while (remaining.Length > 0)
            {
                if (remaining.Length <= width)
                {
                    yield return remaining;
                    yield break;
                }

                int breakAt = width;
                ReadOnlySpan<char> slice = remaining.AsSpan(0, width);
                int lastNewline = slice.LastIndexOf('\n');
                int lastSpace = slice.LastIndexOf(' ');
                if (lastNewline >= 0)
                {
                    breakAt = lastNewline + 1;
                }
                else if (lastSpace > width / 3)
                {
                    breakAt = lastSpace;
                }

                yield return remaining[..breakAt].TrimEnd('\n');
                remaining = remaining[breakAt..].TrimStart('\n').TrimStart(' ');
            }
        }
    }
}