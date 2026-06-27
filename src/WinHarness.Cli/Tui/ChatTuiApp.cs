using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Runtime;

namespace WinHarness.Cli.Tui;

internal sealed class ChatTuiApp
{
    private readonly IApplication _app;
    private readonly IServiceProvider _services;
    private readonly WinHarnessOptions _options;
    private readonly ChatSession _session;
    private readonly ObservableCollection<string> _transcriptLines = [];
    private readonly ObservableCollection<string> _toolLines = [];
    private readonly CancellationTokenSource _shutdownCts;

    private Label _status = null!;
    private ListView _transcript = null!;
    private ListView _tools = null!;
    private TextField _input = null!;
    private bool _turnRunning;
    private int _activeAssistantLineIndex = -1;
    private Task _activeTurn = Task.CompletedTask;

    private ChatTuiApp(
        IApplication app,
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        CancellationTokenSource shutdownCts)
    {
        _app = app;
        _services = services;
        _options = services.GetRequiredService<WinHarnessOptions>();
        _session = new ChatSession(providerId, modelId, renderMarkdown);
        _shutdownCts = shutdownCts;
    }

    public static async ValueTask RunAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IApplication app = Application.Create();
        app.Init();
        ChatTuiApp chat = new(app, services, providerId, modelId, renderMarkdown, shutdownCts);
        using Window window = chat.BuildWindow();
        using CancellationTokenRegistration registration = cancellationToken.Register(static state =>
        {
            IApplication application = (IApplication)state!;
            application.Invoke(() => application.RequestStop());
        }, app);

        chat.AppendSystem("/help for commands · Ctrl+Q to quit · Enter to send");
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
            Width = Dim.Fill()! - 2
        };

        FrameView transcriptFrame = new()
        {
            Title = "Conversation",
            X = 0,
            Y = 1,
            Width = Dim.Percent(70),
            Height = Dim.Fill()! - 4
        };
        _transcript = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _transcript.SetSource(_transcriptLines);
        transcriptFrame.Add(_transcript);

        FrameView toolsFrame = new()
        {
            Title = "Tools",
            X = Pos.Right(transcriptFrame),
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()! - 4
        };
        _tools = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _tools.SetSource(_toolLines);
        toolsFrame.Add(_tools);

        _input = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill()! - 2
        };
        _input.Accepting += (_, args) =>
        {
            args.Handled = true;
            _ = SubmitCurrentInputAsync();
        };

        Bar statusBar = new()
        {
            Orientation = Orientation.Horizontal,
            Y = Pos.AnchorEnd()
        };
        statusBar.Add(new Shortcut
        {
            Title = "_Send",
            HelpText = "Send prompt",
            Key = Key.Enter,
            Action = () => _ = SubmitCurrentInputAsync()
        });
        statusBar.Add(new Shortcut
        {
            Title = "_Clear",
            HelpText = "Clear conversation",
            Key = Key.L.WithCtrl,
            Action = ClearConversation
        });
        statusBar.Add(new Shortcut
        {
            Title = "_Quit",
            HelpText = "Quit",
            Key = Key.Q.WithCtrl,
            Action = RequestQuit
        });

        window.Add(_status, transcriptFrame, toolsFrame, _input, statusBar);
        UpdateStatus();
        return window;
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
            SlashCommandResult result = SlashCommandProcessor.Execute(_options, _session, input);
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
        _session.Conversation.Add(new ConversationMessage(ConversationRole.User, prompt));
        _activeAssistantLineIndex = -1;

        try
        {
            IAgentRuntime runtime = _services.GetRequiredService<IAgentRuntime>();
            await foreach (AgentEvent agentEvent in runtime.RunAsync(
                               new AgentRunRequest(_session.ProviderId, _session.ModelId, _session.Conversation),
                               _shutdownCts.Token).ConfigureAwait(false))
            {
                switch (agentEvent.Kind)
                {
                    case AgentEventKind.ToolActivity:
                        AppendTool(agentEvent.Message);
                        break;
                    case AgentEventKind.Failed:
                        AppendSystem("Error: " + agentEvent.Message);
                        break;
                    case AgentEventKind.Completed:
                        if (agentEvent.AssistantMessage is not null)
                        {
                            _session.Conversation.Add(agentEvent.AssistantMessage);
                        }

                        break;
                    case AgentEventKind.AssistantDelta:
                        AppendAssistantDelta(agentEvent.Message);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            _activeAssistantLineIndex = -1;
            _turnRunning = false;
            SetInputEnabled(true);
        }
    }

    private void ClearConversation()
    {
        if (_turnRunning)
        {
            AppendSystem("Wait for the current turn to finish before clearing.");
            return;
        }

        InvokeUi(() =>
        {
            _session.Conversation.Clear();
            _transcriptLines.Clear();
            _toolLines.Clear();
            RefreshLists();
            _transcriptLines.Add("system: Conversation cleared.");
            RefreshTranscript();
        });
    }

    private void AppendSystem(string message)
    {
        InvokeUi(() =>
        {
            _transcriptLines.Add("system: " + message);
            RefreshTranscript();
        });
    }

    private void AppendUser(string message)
    {
        InvokeUi(() =>
        {
            _transcriptLines.Add("user: " + message);
            RefreshTranscript();
        });
    }

    private void AppendAssistantDelta(string delta)
    {
        InvokeUi(() =>
        {
            if (_activeAssistantLineIndex < 0)
            {
                _transcriptLines.Add("assistant: ");
                _activeAssistantLineIndex = _transcriptLines.Count - 1;
            }

            _transcriptLines[_activeAssistantLineIndex] += delta.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', ' ');
            RefreshTranscript();
        });
    }

    private void AppendTool(string message)
    {
        InvokeUi(() =>
        {
            _toolLines.Add(message);
            RefreshTools();
        });
    }

    private void UpdateStatus()
    {
        InvokeUi(() =>
        {
            _status.Text = $"provider {_session.ProviderId} · model {_session.ModelId} · markdown {(_session.RenderMarkdown ? "on" : "off")}";
        });
    }

    private void SetInputEnabled(bool enabled)
    {
        InvokeUi(() =>
        {
            _input.Enabled = enabled;
            _input.Text = enabled ? string.Empty : "running...";
            _status.Text = $"provider {_session.ProviderId} · model {_session.ModelId} · markdown {(_session.RenderMarkdown ? "on" : "off")}";
        });
    }

    private void RefreshLists()
    {
        RefreshTranscript();
        RefreshTools();
    }

    private void RefreshTranscript()
    {
        _transcript.SetSource(_transcriptLines);
        _transcript.SetNeedsDraw();
    }

    private void RefreshTools()
    {
        _tools.SetSource(_toolLines);
        _tools.SetNeedsDraw();
    }

    private void InvokeUi(Action action)
    {
        _app.Invoke(action);
    }
}
