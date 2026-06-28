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
    private readonly CancellationTokenSource _shutdownCts;

    private Label _status = null!;
    private TranscriptPanel _transcript = null!;
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

        _transcript = new TranscriptPanel
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
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
        RebuildTranscript(scrollToEnd: false);
    }

    private void LoadTranscriptFromSession()
    {
        InvokeUi(() =>
        {
            _messages.Clear();
            PopulateTranscriptMessagesFromSession();
            RebuildTranscript(scrollToEnd: false);
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
            RebuildTranscript(scrollToEnd: false);
        });
    }

    private void PopulateTranscriptMessagesFromSession()
    {
        foreach (ConversationMessage message in _session.Conversation.Messages)
        {
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                continue;
            }

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

    private void RebuildTranscript(bool scrollToEnd)
    {
        int width = GetTranscriptWrapWidth();
        _transcript.SetContentWidth(width);
        _transcript.Rebuild(_messages, _session.RenderMarkdown);
        if (!scrollToEnd)
        {
            return;
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

            if (IsMarkdownToggle(input))
            {
                InvokeUi(() => RebuildTranscript(scrollToEnd: true));
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
            InvokeUi(() =>
            {
                _transcript.ClearActiveAssistant();
                _transcript.FinalizeActiveAssistantLayout();
                RebuildTranscript(scrollToEnd: true);
            });
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
            RebuildTranscript(scrollToEnd: true);
        });
    }

    private void AppendUser(string message)
    {
        InvokeUi(() =>
        {
            _messages.Add(new TranscriptMessage(TranscriptRole.User, message));
            RebuildTranscript(scrollToEnd: true);
        });
    }

    private void AppendAssistantDelta(string delta)
    {
        InvokeUi(() =>
        {
            bool isFirstDelta = _activeAssistantRowIndex < 0;
            if (isFirstDelta)
            {
                _messages.Add(new TranscriptMessage(TranscriptRole.Assistant, string.Empty));
                _activeAssistantRowIndex = _messages.Count - 1;
            }

            TranscriptMessage row = _messages[_activeAssistantRowIndex];
            string normalized = delta.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string text = row.Text + normalized;
            _messages[_activeAssistantRowIndex] = row with { Text = text };

            if (_session.RenderMarkdown)
            {
                if (isFirstDelta)
                {
                    RebuildTranscript(scrollToEnd: true);
                }
                else
                {
                    _transcript.UpdateActiveAssistant(text, renderMarkdown: true);
                }
            }
            else
            {
                RebuildTranscript(scrollToEnd: true);
            }
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
}
