using System.Text;
using WinHarness.Platform;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Pins a fixed header row at the top and a fixed footer row at the bottom of
/// the terminal using a DECSTBM scroll region (<c>ESC [ top ; bottom r</c>),
/// leaving the rows between for scrolling conversation output. Interactive-only;
/// a no-op when output is redirected, the terminal is too short/narrow, or the
/// feature is not opted in, so redirected/one-shot paths are unchanged.
///
/// Phase 0 spike (2026-07-13, <c>spikes/WinHarness.TerminalRegionSpike</c>)
/// proved DECSTBM keeps the fixed rows fixed under the real Windows conhost
/// screen buffer (read back with <c>ReadConsoleOutputW</c>). Phase 1 added the
/// lifecycle skeleton; Phase 2 wires real header/footer content through
/// <see cref="SetHeader"/>/<see cref="SetFooter"/>. Fixed-row writes use DECSC/
/// DECRC (<c>ESC 7</c>/<c>ESC 8</c>) cursor save/restore so painting the header
/// and footer never moves the conversation cursor.
/// </summary>
internal sealed class ScreenRegionController : IDisposable
{
    /// <summary>
    /// Override environment variable. Phase 5 replaces the hard opt-in with a
    /// terminal-capability probe: when unset, the controller auto-enables on
    /// terminals that report virtual-terminal processing (the DECSTBM scroll
    /// region is honored). Set <c>1</c>/<c>true</c> to force on (skip the probe,
    /// for terminals the probe mis-detects), or <c>0</c>/<c>false</c> to force off.
    /// </summary>
    public const string OptInEnvironmentVariable = "WINHARNESS_FIXED_HEADER";

    private ScreenRegionLayout _layout;
    private readonly bool _optedIn;
    private readonly bool _redirected;
    private bool _entered;
    private Encoding? _priorEncoding;
    private string _headerText = string.Empty;
    private string _footerText = string.Empty;

    /// <summary>
    /// How many terminal rows the active prompt currently occupies (1 when idle /
    /// empty). Grows as <see cref="WritePromptInput"/> soft-wraps past the width;
    /// reset by <see cref="EndPrompt"/>.
    /// </summary>
    private int _promptRows = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenRegionController"/>
    /// class. Use <see cref="Create"/> to resolve the layout against the real
    /// console; this constructor is for test seams that already hold a layout.
    /// </summary>
    internal ScreenRegionController(ScreenRegionLayout layout)
        : this(layout, optedIn: layout.Active, redirected: false)
    {
    }

    /// <summary>
    /// Initializes a new instance with the opt-in/redirected flags retained so
    /// <see cref="OnResize"/> can re-resolve the layout after the terminal size
    /// changes.
    /// </summary>
    internal ScreenRegionController(ScreenRegionLayout layout, bool optedIn, bool redirected)
    {
        _layout = layout;
        _optedIn = optedIn;
        _redirected = redirected;
    }

    /// <summary>
    /// Gets the resolved layout. When <see cref="ScreenRegionLayout.Active"/>
    /// is <c>false</c>, every operation is a no-op.
    /// </summary>
    public ScreenRegionLayout Layout => _layout;

    /// <summary>
    /// Gets a value indicating whether the controller has taken over the screen
    /// (region set, fixed rows painted). <c>false</c> when redirected, too short,
    /// or not opted in. Becomes <c>false</c> after <see cref="OnResize"/> if the
    /// terminal shrinks below the minimum size.
    /// </summary>
    public bool IsActive => _layout.Active;

    /// <summary>
    /// Resolves a controller against the current console. When
    /// <see cref="OptInEnvironmentVariable"/> is unset, the controller auto-enables
    /// only if <paramref name="ansiConfigurator"/> reports the terminal processes
    /// virtual-terminal sequences (so DECSTBM is honored); <c>1</c>/<c>true</c>
    /// forces on and <c>0</c>/<c>false</c> forces off. Returns an inactive instance
    /// (every method a no-op) when forced/ probes off, output is redirected, or the
    /// terminal is smaller than <see cref="ScreenRegionLayout.MinimumHeight"/>/
    /// <see cref="ScreenRegionLayout.MinimumWidth"/>.
    /// </summary>
    public static ScreenRegionController Create(IAnsiConsoleConfigurator ansiConfigurator)
    {
        bool optedIn = ResolveOptIn(
            Environment.GetEnvironmentVariable(OptInEnvironmentVariable),
            ansiConfigurator.IsVirtualTerminalEnabled);
        bool redirected = Console.IsOutputRedirected || Console.IsInputRedirected;
        int height = TryGetWindowDimension(Console.WindowHeight, out int h) ? h : -1;
        int width = TryGetWindowDimension(Console.WindowWidth, out int w) ? w : -1;
        return new ScreenRegionController(ScreenRegionLayout.Resolve(optedIn, redirected, width, height), optedIn, redirected);
    }

    /// <summary>
    /// Maps the override environment value plus the probed terminal capability to
    /// the final opt-in decision. Unset → trust the probe; <c>0</c>/<c>false</c> →
    /// off; anything else → on (force). Pure so it is unit-testable headlessly.
    /// </summary>
    internal static bool ResolveOptIn(string? value, bool terminalSupportsVirtualTerminal)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return terminalSupportsVirtualTerminal;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sets the scroll region, clears the screen, paints the current header and
    /// footer, and positions the cursor at the top of the scrolling region for
    /// subsequent writes. No-op when inactive.
    /// </summary>
    public void Enter()
    {
        if (!IsActive || _entered)
        {
            return;
        }

        // Phase 0 finding: raw Console.Write of non-ASCII is transliterated via
        // the console output codepage. Header/footer content includes the middle
        // dot (·) and ellipsis (…); emit as UTF-8 so they render correctly. Capture
        // the prior encoding so Exit can restore it (the startup configurator may
        // have set UTF-8 already; restore keeps teardown symmetric).
        _priorEncoding = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.UTF8;

        Console.CursorVisible = false;
        // Alternate screen buffer so Exit can restore the caller's prior
        // terminal contents instead of leaving fixed-row chrome behind.
        Console.Write("\x1b[?1049h");
        Console.Write("\x1b[2J\x1b[H");
        SetRegion(Layout.ScrollTop, Layout.ScrollBottom);
        Repaint();
        Console.Write($"\x1b[{Layout.ScrollTop};1H");
        _entered = true;
    }

    /// <summary>
    /// Sets the header text (plain text; Spectre markup is not interpreted in
    /// the fixed row) and repaints the fixed header row as a blue accent bar,
    /// truncated/padded to the terminal width. No-op when inactive.
    /// </summary>
    public void SetHeader(string text)
    {
        _headerText = text;
        if (IsActive)
        {
            WriteBar(1, _headerText, BarStyle.Header);
        }
    }

    /// <summary>
    /// Sets the footer status text (plain text) and repaints it as a cyan
    /// accent bar on the status row, truncated/padded to the terminal width.
    /// No-op when inactive.
    /// </summary>
    public void SetFooter(string text)
    {
        _footerText = text;
        if (IsActive)
        {
            WriteBar(Layout.FooterStatusRow, _footerText, BarStyle.Status);
        }
    }

    /// <summary>
    /// Repaints the fixed header and footer status bars from the last text set
    /// via <see cref="SetHeader"/>/<see cref="SetFooter"/>. No-op when inactive.
    /// </summary>
    public void Repaint()
    {
        if (!IsActive)
        {
            return;
        }

        WriteBar(1, _headerText, BarStyle.Header);
        WriteBar(Layout.FooterStatusRow, _footerText, BarStyle.Status);
    }

    /// <summary>
    /// Recomputes the region after a terminal resize and repaints the fixed
    /// rows. When the terminal has shrunk below
    /// <see cref="ScreenRegionLayout.MinimumHeight"/>/
    /// <see cref="ScreenRegionLayout.MinimumWidth"/>, the controller deactivates
    /// (resets the scroll region to the full screen) so the chat falls back to
    /// the shipped scrolling status-line behavior. Safe to call from the idle and
    /// steering input loops. No-op when the feature was never opted in.
    /// </summary>
    public void OnResize()
    {
        // Never opted in (test seam or env unset) — nothing to track.
        if (!_optedIn)
        {
            return;
        }

        int height = TryGetWindowDimension(Console.WindowHeight, out int h) ? h : -1;
        int width = TryGetWindowDimension(Console.WindowWidth, out int w) ? w : -1;
        ScreenRegionLayout next = ScreenRegionLayout.Resolve(_optedIn, _redirected, width, height);
        if (next == _layout)
        {
            return;
        }

        if (!next.Active)
        {
            // Terminal shrank below the minimum (or went redirected): reset the
            // scroll region and deactivate so callers fall back to the scrolling
            // status-line path. Fixed rows already painted are left to scroll
            // away naturally.
            Console.Write("\x1b[r");
            Console.CursorVisible = true;
            _promptRows = 1;
            _layout = next;
            return;
        }

        // Resize while active: re-establish the region against the new size and
        // repaint the fixed rows. Conversation content already on screen is not
        // reflowed (DECSTBM does not track it); acceptable for an opt-in feature.
        // Prompt wrap is recomputed on the next WritePromptInput keystroke.
        _promptRows = 1;
        _layout = next;
        Console.Write("\x1b[2J\x1b[H");
        SetRegion(next.ScrollTop, next.ScrollBottom);
        Repaint();
        Console.Write($"\x1b[{next.ScrollTop};1H");
    }

    /// <summary>
    /// Saves the conversation cursor (DECSC) and paints an empty prompt on the
    /// fixed footer so the subsequent key loop can echo typed input without
    /// scrolling the conversation region. Pair with <see cref="EndPrompt"/>.
    /// No-op when inactive.
    /// </summary>
    public void BeginPrompt()
    {
        if (!IsActive)
        {
            return;
        }

        // DECSC first so EndPrompt can restore the conversation cursor. Then
        // paint the empty prompt (prefix only) via WritePromptInput.
        Console.Write("\u001b7");
        WritePromptInput(string.Empty);
    }

    /// <summary>
    /// Repaints the fixed prompt with <paramref name="buffer"/> soft-wrapped to
    /// the terminal width. When the wrap needs more than one row, the scroll
    /// region shrinks and the status/prompt block grows upward so typing past the
    /// edge continues on the next line. Call after every keystroke while the
    /// prompt is active. No-op when inactive.
    /// </summary>
    public void WritePromptInput(string buffer)
    {
        if (!IsActive)
        {
            return;
        }

        IReadOnlyList<string> wrapped = PromptLineView.Wrap(buffer, Layout.Width);
        if (wrapped.Count == 0)
        {
            return;
        }

        // Keep at least one scrolling row so DECSTBM stays valid (top <= bottom).
        int maxPromptRows = Math.Max(1, Layout.Height - ScreenRegionLayout.HeaderRows - 1 /* status */ - 1 /* scroll */);
        int start = wrapped.Count > maxPromptRows ? wrapped.Count - maxPromptRows : 0;
        int promptRows = wrapped.Count - start;
        int statusRow = Layout.Height - promptRows;
        int scrollBottom = statusRow - 1;

        if (promptRows != _promptRows)
        {
            // Growing: steal rows from the scroll region. Shrinking: give them
            // back. Clear abandoned prompt rows when shrinking so stale glyphs
            // do not linger above the new status line.
            if (promptRows < _promptRows)
            {
                int oldStatusRow = Layout.Height - _promptRows;
                for (int row = oldStatusRow; row < statusRow; row++)
                {
                    Console.Write($"\u001b[{row};1H\u001b[2K");
                }
            }

            SetRegion(Layout.ScrollTop, scrollBottom);
            _promptRows = promptRows;
        }

        // Paint status + prompt without DECSC/DECRC — BeginPrompt already holds
        // the conversation cursor in the single DECSC slot. Status is a cyan
        // accent bar; prompt rows stay normal so typing reads clearly.
        Console.Write($"\u001b[{statusRow};1H\u001b[2K{FormatBar(_footerText, Layout.Width, BarStyle.Status)}");
        for (int i = 0; i < promptRows; i++)
        {
            Console.Write($"\u001b[{statusRow + 1 + i};1H\u001b[2K{wrapped[start + i]}");
        }
    }

    /// <summary>
    /// Clears the prompt block, restores the default one-row footer layout, and
    /// restores the conversation cursor saved by <see cref="BeginPrompt"/>
    /// (DECRC). Safe to call when inactive or when <see cref="BeginPrompt"/> was
    /// not called. No-op when inactive.
    /// </summary>
    public void EndPrompt()
    {
        if (!IsActive)
        {
            return;
        }

        int statusRow = Layout.Height - _promptRows;
        for (int row = statusRow; row <= Layout.Height; row++)
        {
            Console.Write($"\u001b[{row};1H\u001b[2K");
        }

        _promptRows = 1;
        SetRegion(Layout.ScrollTop, Layout.ScrollBottom);
        // Put the status bar back on its default row (Height - 1).
        Console.Write($"\u001b[{Layout.FooterStatusRow};1H\u001b[2K{FormatBar(_footerText, Layout.Width, BarStyle.Status)}");
        Console.Write("\u001b8");
    }

    /// <summary>
    /// Restores the terminal: resets the scroll region, leaves the alternate
    /// screen buffer (bringing back the caller's prior contents), shows the
    /// cursor, and restores the output encoding captured by <see cref="Enter"/>.
    /// Idempotent — keys off the internal entered flag (not <see cref="IsActive"/>),
    /// so it still restores after <see cref="OnResize"/> deactivated the layout
    /// mid-session. Safe to call multiple times; a no-op when <see cref="Enter"/>
    /// never ran (inactive controller).
    /// </summary>
    public void Exit()
    {
        if (!_entered)
        {
            return;
        }

        _entered = false;
        _promptRows = 1;

        // Reset scroll region first (parameterless ESC[r works even when the
        // layout was deactivated mid-session), then leave the alternate screen
        // so the fixed header/footer/prompt chrome disappears with the buffer.
        Console.Write("\x1b[r\x1b[?1049l");
        Console.CursorVisible = true;

        if (_priorEncoding is not null)
        {
            try
            {
                Console.OutputEncoding = _priorEncoding;
            }
            catch (IOException)
            {
                // Output redirected away from a console mid-session; leave the
                // encoding as-is rather than failing teardown.
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Best-effort: swallows console exceptions so a teardown failure on an exit /
    /// exception / Ctrl+C path can never leave the process in a worse state than
    /// the region already being restored.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            Exit();
        }
        catch (IOException)
        {
            // Console gone (redirected/closed) during teardown — nothing to restore.
        }
        catch (PlatformNotSupportedException)
        {
            // Encoding/console APIs unavailable on this host during teardown.
        }
    }

    private static bool TryGetWindowDimension(int value, out int resolved)
    {
        // Console.WindowWidth/Height throw IOException when no console is attached
        // (redirected/headless) and may return 0 in some embedded terminals.
        resolved = value;
        return value > 0;
    }

    private static void SetRegion(int top, int bottom) => Console.Write($"\x1b[{top};{bottom}r");

    private static string Truncate(string text, int width)
    {
        if (text.Length <= width)
        {
            return text;
        }

        if (width <= 1)
        {
            return width <= 0 ? string.Empty : text[..width];
        }

        return text[..(width - 1)] + "…";
    }

    private enum BarStyle
    {
        Header,
        Status,
    }

    /// <summary>
    /// Truncates/pads <paramref name="text"/> to <paramref name="width"/> and wraps
    /// it in a solid accent-bar SGR (blue header / cyan status, bright white text)
    /// so the chrome reads apart from the scrolling conversation.
    /// </summary>
    private static string FormatBar(string text, int width, BarStyle style)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        string body = Truncate(text, width);
        if (body.Length < width)
        {
            body = body.PadRight(width);
        }

        // 44 = blue bg, 46 = cyan bg, 97 = bright white fg. Reset with SGR 0 so
        // lingering reverse/intensity from other writers cannot leak into the bar.
        string open = style == BarStyle.Header ? "\x1b[44;97m" : "\x1b[46;97m";
        return $"{open}{body}\x1b[0m";
    }

    /// <summary>
    /// Paints a colored fixed bar: save the cursor (DECSC), move to
    /// <paramref name="row"/>, clear the line, write the bar, then restore the
    /// cursor (DECRC) so the conversation cursor is undisturbed.
    /// </summary>
    private void WriteBar(int row, string text, BarStyle style) =>
        Console.Write($"\u001b7\u001b[{row};1H\u001b[2K{FormatBar(text, Layout.Width, style)}\u001b8");
}

/// <summary>
/// Pure, console-free layout decision for <see cref="ScreenRegionController"/>.
/// Separating it from console I/O makes the opt-in/redirect/size math unit
/// testable headlessly.
/// </summary>
internal readonly record struct ScreenRegionLayout(bool Active, int Width, int Height, int ScrollTop, int ScrollBottom)
{
    /// <summary>One fixed header row at the top.</summary>
    public const int HeaderRows = 1;

    /// <summary>Two fixed footer rows at the bottom (status + prompt).</summary>
    public const int FooterRows = 2;

    /// <summary>
    /// Smallest terminal height that leaves at least three scrolling rows between
    /// the fixed header and footer.
    /// </summary>
    public const int MinimumHeight = HeaderRows + FooterRows + 3;

    /// <summary>Smallest terminal width worth enabling the fixed rows for.</summary>
    public const int MinimumWidth = 20;

    /// <summary>
    /// Gets the fixed row holding the footer status line (one above the prompt
    /// row). Meaningful only when <see cref="Active"/> is <c>true</c>.
    /// </summary>
    public int FooterStatusRow => Height - 1;

    /// <summary>
    /// Gets the fixed row holding the input prompt (the last terminal row).
    /// Meaningful only when <see cref="Active"/> is <c>true</c>.
    /// </summary>
    public int FooterPromptRow => Height;

    /// <summary>
    /// Resolves the layout from the opt-in flag, redirected state, and terminal
    /// size. Returns an inactive layout when the feature is off, redirected, or
    /// the terminal is too small.
    /// </summary>
    public static ScreenRegionLayout Resolve(bool optedIn, bool redirected, int width, int height)
    {
        if (!optedIn || redirected || height < MinimumHeight || width < MinimumWidth)
        {
            return default;
        }

        int top = HeaderRows + 1; // header occupies row 1; region starts at row 2.
        int bottom = height - FooterRows; // leave the last two rows for the footer.
        return new ScreenRegionLayout(true, width, height, top, bottom);
    }
}
