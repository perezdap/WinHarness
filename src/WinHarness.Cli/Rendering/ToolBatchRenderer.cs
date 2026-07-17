using System.Globalization;
using Spectre.Console;
using WinHarness.Runtime;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Coalesces a flurry of tool activity events into a single live spinner line
/// during a batch and one settled summary line per batch on completion, so a
/// run that issues many rapid shell probes no longer floods the scrollback
/// with one line per call. Verbose mode restores the original one-line-per-event
/// rendering for debugging.
/// </summary>
internal sealed class ToolBatchRenderer
{
    private const string PendingIcon = "[dim]⠋[/]";
    private const string SuccessIcon = "[green]✓[/]";
    private const string FailedIcon = "[red]✗[/]";
    private const string MixedIcon = "[yellow]~[/]";

    private readonly bool _verbose;
    private readonly IAnsiConsole _console;
    private int _active;
    private int _calls;
    private int _ok;
    private int _failed;
    private TimeSpan _duration;

    public ToolBatchRenderer(bool verbose, IAnsiConsole? console = null)
    {
        _verbose = verbose;
        _console = console ?? AnsiConsole.Console;
    }

    /// <summary>
    /// True while a batch is in progress (started events seen but not yet
    /// settled). Used by the caller to decide whether to refresh the
    /// <see cref="ThinkingIndicator"/> label.
    /// </summary>
    public bool HasPendingBatch => _active > 0 || _calls > 0;

    /// <summary>
    /// Label the live spinner should display. The indicator itself owns the
    /// elapsed timer, so this label only describes the current batch.
    /// </summary>
    public string LiveLabel
    {
        get
        {
            if (!HasPendingBatch)
            {
                return "thinking";
            }

            if (_active > 0)
            {
                string tools = _active == 1 ? "tool" : "tools";
                return _calls == 0
                    ? $"running {_active} {tools}"
                    : $"running {_active} {tools} · {_ok} ok · {_failed} failed";
            }

            return $"tool activity · {_ok} ok · {_failed} failed";
        }
    }

    /// <summary>
    /// Records one tool activity event from the runtime. In compact mode,
    /// started events update the live spinner but do not emit a line. In verbose
    /// mode, every event prints a persistent line.
    /// </summary>
    public void OnEvent(ToolActivityInfo info)
    {
        if (_verbose)
        {
            RenderVerboseLine(info);
            return;
        }

        switch (info.Phase)
        {
            case ToolActivityPhase.Started:
                _active++;
                break;

            case ToolActivityPhase.Completed:
            case ToolActivityPhase.Failed:
                if (_active > 0)
                {
                    _active--;
                }

                _calls++;
                if (info.Duration is { } duration)
                {
                    _duration += duration;
                }

                if (info.Phase == ToolActivityPhase.Failed || info.Succeeded == false)
                {
                    _failed++;
                }
                else
                {
                    _ok++;
                }

                break;
        }
    }

    /// <summary>
    /// Writes one summary line for the in-flight batch and clears counters.
    /// No-op when no batch is pending.
    /// </summary>
    /// <param name="terminal">
    /// <c>true</c> when the turn has ended or failed, so unfinished tools can no
    /// longer receive completion events and are reported as <em>interrupted</em>.
    /// <c>false</c> for an interim flush between assistant-text segments, where
    /// unfinished tools are still executing and are reported as <em>running</em>.
    /// </param>
    public void Settle(bool terminal = false)
    {
        if (!HasPendingBatch)
        {
            return;
        }

        int calls = _calls + _active;
        int ok = _ok;
        int failed = _failed;
        // Tools still executing when the batch settles are never folded into the
        // failed count — conflating not-yet-finished with failed is user-visible
        // misinformation, and they have contributed no duration yet. On an interim
        // flush they are still live ("running"); at terminal settlement they can
        // no longer complete, so they are "interrupted".
        int unfinished = _active;
        TimeSpan duration = _duration;
        _active = 0;
        _calls = 0;
        _ok = 0;
        _failed = 0;
        _duration = TimeSpan.Zero;

        string durationText = FormatDuration(duration);
        string header = calls == 1
            ? "[bold]tool run[/]"
            : $"[bold]{calls} tool runs[/]";

        string unfinishedText = unfinished > 0
            ? $" · {unfinished} {(terminal ? "interrupted" : "running")}"
            : string.Empty;

        _console.MarkupLine(
            $"{IconFor(ok, failed, unfinished)} {header} [dim]· {ok} ok · {failed} failed{unfinishedText} · {durationText}[/]");
    }

    /// <summary>
    /// Sets a one-line descriptor for a tool call when verbose mode is on.
    /// Falls back to the tool name when no display label was supplied.
    /// </summary>
    private void RenderVerboseLine(ToolActivityInfo info)
    {
        string label = Markup.Escape(info.DisplayLabel ?? info.ToolName);

        switch (info.Phase)
        {
            case ToolActivityPhase.Started:
                _console.MarkupLine($"{PendingIcon} [bold]{label}[/]");
                break;

            case ToolActivityPhase.Completed:
            {
                string icon = info.Succeeded == false ? FailedIcon : SuccessIcon;
                string duration = FormatDuration(info.Duration);
                _console.MarkupLine($"{icon} [bold]{label}[/] [dim]({duration})[/]");
                break;
            }

            case ToolActivityPhase.Failed:
            {
                string duration = FormatDuration(info.Duration);
                string exc = info.ExceptionTypeName is null
                    ? ""
                    : $" [red]{Markup.Escape(info.ExceptionTypeName)}[/]";
                _console.MarkupLine($"{FailedIcon} [bold]{label}[/] [dim]({duration})[/]{exc}");
                break;
            }
        }
    }

    private static string IconFor(int ok, int failed, int running)
    {
        if (failed == 0 && running == 0)
        {
            return SuccessIcon;
        }

        if (failed > 0 && ok == 0 && running == 0)
        {
            return FailedIcon;
        }

        return MixedIcon;
    }

    private static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "?";
        }

        double ms = duration.Value.TotalMilliseconds;
        if (ms >= 1000)
        {
            return duration.Value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s";
        }

        return ms.ToString("F0", CultureInfo.InvariantCulture) + " ms";
    }
}