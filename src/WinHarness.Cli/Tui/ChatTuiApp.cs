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
using WinHarness.Conversation;
using WinHarness.Runtime;

using Attribute = Terminal.Gui.Drawing.Attribute;

namespace WinHarness.Cli.Tui;

internal sealed class ChatTuiApp
{
    private readonly IApplication _app;
    private readonly IServiceProvider _services;
    private readonly WinHarnessOptions _options;
    private readonly ChatSession _session;
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
            Height = Dim.Fill()! - 4
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
            Source = new TranscriptDataSource(_transcriptRows)
        };
        transcriptFrame.Add(_transcript);

        FrameView toolsFrame = new()
        {
            Title = "Tools",
            X = Pos.Right(transcriptFrame),
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()! - 4
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

        _input = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill()! - 2
        };
        _input.SetScheme(new Scheme
        {
            Normal = new Attribute(Color.BrightGreen, Color.Black),
            Focus = new Attribute(Color.Black, Color.BrightGreen),
            Editable = new Attribute(Color.BrightGreen, Color.Black)
        });
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
        WinHarness.Conversation.Conversation runConversation = _session.CreateRunConversation(prompt);
        _activeAssistantRowIndex = -1;

        try
        {
            IAgentRuntime runtime = _services.GetRequiredService<IAgentRuntime>();
            await foreach (AgentEvent agentEvent in runtime.RunAsync(
                               new AgentRunRequest(_session.ProviderId, _session.ModelId, runConversation),
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
                        if (agentEvent.AssistantMessage is not null)
                        {
                            _session.Conversation.Add(new ConversationMessage(ConversationRole.User, prompt));
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

            UpdateToolStatus("idle");
        }
        finally
        {
            _activeAssistantRowIndex = -1;
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
            _transcriptRows.Clear();
            UpdateToolStatus("idle");
            RefreshTranscript();
            _transcriptRows.Add(new TranscriptRow(TranscriptRole.System, "Conversation cleared."));
            RefreshTranscript();
        });
    }

    private void AppendSystem(string message)
    {
        InvokeUi(() =>
        {
            _transcriptRows.Add(new TranscriptRow(TranscriptRole.System, message));
            RefreshTranscript();
        });
    }

    private void AppendUser(string message)
    {
        InvokeUi(() =>
        {
            _transcriptRows.Add(new TranscriptRow(TranscriptRole.User, message));
            RefreshTranscript();
        });
    }

    private void AppendAssistantDelta(string delta)
    {
        InvokeUi(() =>
        {
            if (_activeAssistantRowIndex < 0)
            {
                _transcriptRows.Add(new TranscriptRow(TranscriptRole.Assistant, string.Empty));
                _activeAssistantRowIndex = _transcriptRows.Count - 1;
            }

            TranscriptRow row = _transcriptRows[_activeAssistantRowIndex];
            string cleaned = delta.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', ' ');
            _transcriptRows[_activeAssistantRowIndex] = row with { Text = row.Text + cleaned };
            RefreshTranscript();
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
            _status.Text = BuildStatusText();
        });
    }

    private string BuildStatusText()
    {
        string skill = _session.SelectedSkill is null ? "none" : _session.SelectedSkill.Name;
        return $"provider {_session.ProviderId} · model {_session.ModelId} · markdown {(_session.RenderMarkdown ? "on" : "off")} · skill {skill}";
    }

    private void SetInputEnabled(bool enabled)
    {
        InvokeUi(() =>
        {
            _input.Enabled = enabled;
            _input.Text = enabled ? string.Empty : "running...";
            _status.Text = BuildStatusText();
        });
    }

    private void RefreshTranscript()
    {
        _transcript.SetNeedsDraw();
    }

    private void InvokeUi(Action action)
    {
        _app.Invoke(action);
    }

    /// <summary>A single transcript line tagged with the role that produced it, used for color-coded rendering.</summary>
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

            string prefix = row.Role switch
            {
                TranscriptRole.System => "system › ",
                TranscriptRole.User => "you › ",
                TranscriptRole.Assistant => "ai › ",
                _ => string.Empty
            };

            string text = prefix + row.Text;
            if (text.Length > width)
            {
                text = width <= 1 ? text[..width] : string.Concat(text.AsSpan(0, width - 1), "…");
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
}
