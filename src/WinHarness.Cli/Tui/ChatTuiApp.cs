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
        chat.AppendSystem("/help for commands · Ctrl+Q to quit · Enter to send · click › line to type");
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
            Normal = new Attribute(Color.BrightYellow, Color.Black)
        });

        FrameView transcriptFrame = new()
        {
            Title = "Conversation",
            X = 0,
            Y = 1,
            Width = Dim.Percent(70),
            Height = Dim.Fill()! - 4,
            CanFocus = false,
            TabStop = TabBehavior.NoStop
        };
        transcriptFrame.SetScheme(new Scheme
        {
            Normal = new Attribute(Color.BrightCyan, Color.Black),
            Focus = new Attribute(Color.Black, Color.BrightCyan)
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
            Normal = new Attribute(Color.BrightMagenta, Color.Black),
            Focus = new Attribute(Color.Black, Color.BrightMagenta)
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
            Normal = new Attribute(Color.Gray, Color.Black)
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
            Normal = new Attribute(Color.BrightGreen, Color.Black)
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
            Normal = new Attribute(Color.White, Color.Black),
            Focus = new Attribute(Color.Black, Color.BrightGreen),
            Editable = new Attribute(Color.White, Color.Black)
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
        foreach (TranscriptMessage message in _messages)
        {
            foreach (string line in TranscriptWrap.Wrap(message.Role, message.Text, width))
            {
                _transcriptRows.Add(new TranscriptRow(message.Role, line));
            }
        }

        if (scrollToEnd && _transcriptRows.Count > 0)
        {
            _transcript.MoveEnd(false);
        }

        _transcript.SetNeedsDraw();
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
            string cleaned = delta.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', ' ');
            _messages[_activeAssistantRowIndex] = row with { Text = row.Text + cleaned };
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
    private sealed record TranscriptRow(TranscriptRole Role, string Text);

    /// <summary>Identifies the source of a transcript line so it can be colored distinctly.</summary>
    private enum TranscriptRole
    {
        System,
        User,
        Assistant
    }

    /// <summary>
    /// Custom <see cref="IListDataSource"/> that renders each transcript row with a color and prefix
    /// appropriate to its role, improving readability over the plain black-and-white list.
    /// </summary>
    private sealed class TranscriptDataSource : IListDataSource, IDisposable
    {
        private readonly ObservableCollection<TranscriptRow> _rows;
        private readonly Attribute _systemAttr;
        private readonly Attribute _userAttr;
        private readonly Attribute _assistantAttr;
        private readonly Attribute _selectedAttr;
        private bool _disposed;

        public TranscriptDataSource(ObservableCollection<TranscriptRow> rows)
        {
            _rows = rows;
            _systemAttr = new Attribute(Color.Gray, Color.Black);
            _userAttr = new Attribute(Color.BrightCyan, Color.Black);
            _assistantAttr = new Attribute(Color.BrightGreen, Color.Black);
            _selectedAttr = new Attribute(Color.Black, Color.BrightYellow);
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

            Attribute attr = selected
                ? _selectedAttr
                : row.Role switch
                {
                    TranscriptRole.System => _systemAttr,
                    TranscriptRole.User => _userAttr,
                    TranscriptRole.Assistant => _assistantAttr,
                    _ => _systemAttr
                };

            container.SetAttribute(attr);

            string text = row.Text;
            if (text.Length > width)
            {
                text = text[..width];
            }

            container.AddStr(text);

            int remaining = width - text.Length;
            if (remaining > 0)
            {
                container.AddStr(new string(' ', remaining));
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
        public static IEnumerable<string> Wrap(TranscriptRole role, string text, int width)
        {
            string prefix = role switch
            {
                TranscriptRole.System => "system › ",
                TranscriptRole.User => "you › ",
                TranscriptRole.Assistant => "ai › ",
                _ => string.Empty
            };

            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            int contentWidth = Math.Max(1, width - prefix.Length);
            string indent = new string(' ', prefix.Length);

            bool first = true;
            foreach (string chunk in WordWrap(normalized, contentWidth))
            {
                yield return first ? prefix + chunk : indent + chunk;
                first = false;
            }

            if (first)
            {
                yield return prefix;
            }
        }

        private static IEnumerable<string> WordWrap(string text, int width)
        {
            if (text.Length == 0)
            {
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