using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Animated, self-overwriting "thinking" line shown while the agent is waiting
/// for the model or running a tool. It renders a spinner, a label (default
/// "thinking", or the latest tool activity), and elapsed seconds on a single
/// line that is wiped before any real output is written. No-op when output is
/// redirected so piped/captured output stays clean.
/// </summary>
internal sealed class ThinkingIndicator : IAsyncDisposable
{
    private static readonly char[] Frames =
        ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

    private readonly bool _enabled = !Console.IsOutputRedirected;
    private readonly object _gate = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _frame;
    private int _renderedWidth;
    private string _label = "thinking";

    public void Start()
    {
        if (!_enabled || _loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _loop = Task.Run(
            async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        lock (_gate)
                        {
                            Render();
                        }

                        await Task.Delay(120, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            token);
    }

    public void SetLabel(string label)
    {
        lock (_gate)
        {
            _label = Sanitize(label);
        }
    }

    public async ValueTask StopAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_gate)
        {
            Erase();
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void Render()
    {
        char frame = Frames[_frame % Frames.Length];
        _frame++;

        string elapsed = _stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        string text = Truncate($"{frame} {_label} {elapsed}s");

        Console.Write('\r');
        AnsiConsole.Markup("[dim]" + Markup.Escape(text) + "[/]");

        if (text.Length < _renderedWidth)
        {
            Console.Write(new string(' ', _renderedWidth - text.Length));
        }

        _renderedWidth = text.Length;
    }

    private void Erase()
    {
        if (_renderedWidth == 0)
        {
            return;
        }

        Console.Write('\r');
        Console.Write(new string(' ', _renderedWidth));
        Console.Write('\r');
        _renderedWidth = 0;
    }

    private static string Sanitize(string message)
    {
        string single = message.Replace('\r', ' ').Replace('\n', ' ');
        return single.Length == 0 ? "thinking" : single;
    }

    private static string Truncate(string message)
    {
        int max;
        try
        {
            max = Console.WindowWidth - 1;
        }
        catch (IOException)
        {
            return message;
        }

        if (max <= 0 || message.Length <= max)
        {
            return message;
        }

        return max <= 1 ? message[..max] : string.Concat(message.AsSpan(0, max - 1), "…");
    }
}
