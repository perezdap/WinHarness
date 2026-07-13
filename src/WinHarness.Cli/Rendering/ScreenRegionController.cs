using System.Text;

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
    /// Opt-in environment variable. Phase 1-2 stage the feature behind this flag
    /// so the default chat experience is unchanged; Phase 5 replaces it with a
    /// terminal-capability probe and the flag becomes a force-off override.
    /// </summary>
    public const string OptInEnvironmentVariable = "WINHARNESS_FIXED_HEADER";

    /// <summary>
    /// Initializes a new instance of the <see cref="ScreenRegionController"/>
    /// class. Use <see cref="Create"/> to resolve the layout against the real
    /// console; this constructor is for test seams that already hold a layout.
    /// </summary>
    internal ScreenRegionController(ScreenRegionLayout layout)
    {
        Layout = layout;
    }

    /// <summary>
    /// Gets the resolved layout. When <see cref="ScreenRegionLayout.Active"/>
    /// is <c>false</c>, every operation is a no-op.
    /// </summary>
    public ScreenRegionLayout Layout { get; }

    private string _headerText = string.Empty;
    private string _footerText = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the controller has taken over the screen
    /// (region set, fixed rows painted). <c>false</c> when redirected, too short,
    /// or not opted in.
    /// </summary>
    public bool IsActive => Layout.Active;

    /// <summary>
    /// Resolves a controller against the current console. Returns an inactive
    /// instance (every method a no-op) when output is redirected, the terminal is
    /// smaller than <see cref="ScreenRegionLayout.MinimumHeight"/>/
    /// <see cref="ScreenRegionLayout.MinimumWidth"/>, or
    /// <see cref="OptInEnvironmentVariable"/> is unset/0.
    /// </summary>
    public static ScreenRegionController Create()
    {
        bool optedIn = IsOptedIn(Environment.GetEnvironmentVariable(OptInEnvironmentVariable));
        bool redirected = Console.IsOutputRedirected || Console.IsInputRedirected;
        int height = TryGetWindowDimension(Console.WindowHeight, out int h) ? h : -1;
        int width = TryGetWindowDimension(Console.WindowWidth, out int w) ? w : -1;
        return new ScreenRegionController(ScreenRegionLayout.Resolve(optedIn, redirected, width, height));
    }

    /// <summary>
    /// Sets the scroll region, clears the screen, paints the current header and
    /// footer, and positions the cursor at the top of the scrolling region for
    /// subsequent writes. No-op when inactive.
    /// </summary>
    public void Enter()
    {
        if (!IsActive)
        {
            return;
        }

        // Phase 0 finding: raw Console.Write of non-ASCII is transliterated via
        // the console output codepage. Header/footer content includes the middle
        // dot (·) and ellipsis (…); emit as UTF-8 so they render correctly.
        Console.OutputEncoding = Encoding.UTF8;

        Console.CursorVisible = false;
        Console.Write("\x1b[2J\x1b[H");
        SetRegion(Layout.ScrollTop, Layout.ScrollBottom);
        Repaint();
        Console.Write($"\x1b[{Layout.ScrollTop};1H");
    }

    /// <summary>
    /// Sets the header text (plain text; Spectre markup is not interpreted in
    /// the fixed row) and repaints the fixed header row, truncated to the
    /// terminal width. No-op when inactive.
    /// </summary>
    public void SetHeader(string text)
    {
        _headerText = text;
        if (IsActive)
        {
            WriteFixed(1, Truncate(text, Layout.Width));
        }
    }

    /// <summary>
    /// Sets the footer text (plain text) on the last terminal row, truncated to
    /// the terminal width, and repaints it. No-op when inactive.
    /// </summary>
    public void SetFooter(string text)
    {
        _footerText = text;
        if (IsActive)
        {
            WriteFixed(Layout.Height, Truncate(text, Layout.Width));
        }
    }

    /// <summary>
    /// Repaints the fixed header and footer rows from the last text set via
    /// <see cref="SetHeader"/>/<see cref="SetFooter"/>. No-op when inactive.
    /// </summary>
    public void Repaint()
    {
        if (!IsActive)
        {
            return;
        }

        WriteFixed(1, Truncate(_headerText, Layout.Width));
        WriteFixed(Layout.Height, Truncate(_footerText, Layout.Width));
    }

    /// <summary>
    /// Recomputes the region after a terminal resize and repaints the fixed
    /// rows. Phase 2 stub: resize handling lands in Phase 4.
    /// </summary>
    public void OnResize()
    {
        // Intentionally a no-op for now; Phase 4 polls Console.WindowHeight from
        // the input/steering loops and calls this to reset the region.
    }

    /// <summary>
    /// Restores the terminal: resets the scroll region to the full height and
    /// shows the cursor. Safe to call multiple times. No-op when inactive.
    /// </summary>
    public void Exit()
    {
        if (!IsActive)
        {
            return;
        }

        SetRegion(1, Layout.Height);
        Console.CursorVisible = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Exit();
    }

    private static bool IsOptedIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Paints a fixed row: save the cursor (DECSC), move to <paramref name="row"/>,
    /// clear the line, write the text, then restore the cursor (DECRC) so the
    /// conversation cursor is undisturbed.
    /// </summary>
    private static void WriteFixed(int row, string text) => Console.Write($"\x1b7\x1b[{row};1H\x1b[2K{text}\x1b8");
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
