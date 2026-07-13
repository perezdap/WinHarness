using System.Text;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Pins a fixed header row at the top and a fixed footer row at the bottom of
/// the terminal using a DECSTBM scroll region (<c>ESC [ top ; bottom r</c>),
/// leaving the rows between for scrolling conversation output. Interactive-only;
/// a no-op when output is redirected, the terminal is too short, or the feature
/// is not opted in, so redirected/one-shot paths are unchanged.
///
/// Phase 0 spike (2026-07-13, <c>spikes/WinHarness.TerminalRegionSpike</c>)
/// proved DECSTBM keeps the fixed rows fixed under the real Windows conhost
/// screen buffer (read back with <c>ReadConsoleOutputW</c>). This controller is
/// the Phase 1 lifecycle skeleton: enter/exit, set region, repaint header/footer,
/// and guaranteed teardown. Header/footer <em>content</em> (banner, live status)
/// and routing streaming output through the region land in Phases 2-3.
/// </summary>
internal sealed class ScreenRegionController : IDisposable
{
    /// <summary>
    /// Opt-in environment variable. Phase 1 stages the feature behind this flag
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
    /// instance (every method a no-op) when output is redirected, the terminal
    /// is shorter than <see cref="ScreenRegionLayout.MinimumHeight"/>, or
    /// <see cref="OptInEnvironmentVariable"/> is unset/0.
    /// </summary>
    public static ScreenRegionController Create()
    {
        bool optedIn = IsOptedIn(Environment.GetEnvironmentVariable(OptInEnvironmentVariable));
        bool redirected = Console.IsOutputRedirected || Console.IsInputRedirected;
        int height = TryGetWindowHeight(out int h) ? h : -1;
        return new ScreenRegionController(ScreenRegionLayout.Resolve(optedIn, redirected, height));
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
        // the console output codepage. Header/footer content in later phases may
        // include box glyphs/emoji; emit as UTF-8 so they render correctly.
        Console.OutputEncoding = Encoding.UTF8;

        Console.CursorVisible = false;
        Console.Write("\x1b[2J\x1b[H");
        SetRegion(Layout.ScrollTop, Layout.ScrollBottom);
        Repaint();
        Console.Write($"\x1b[{Layout.ScrollTop};1H");
    }

    /// <summary>
    /// Sets the header text (plain text; Spectre markup is not interpreted in
    /// the fixed row yet) and repaints the fixed header row. No-op when inactive.
    /// </summary>
    public void SetHeader(string text)
    {
        _headerText = text;
        if (IsActive)
        {
            WriteFixed(1, text);
        }
    }

    /// <summary>
    /// Sets the footer text (plain text) on the last terminal row and repaints
    /// it. No-op when inactive.
    /// </summary>
    public void SetFooter(string text)
    {
        _footerText = text;
        if (IsActive)
        {
            WriteFixed(Layout.Height, text);
        }
    }

    /// <summary>
    /// Repaints the fixed header and footer rows. No-op when inactive.
    /// </summary>
    public void Repaint()
    {
        if (!IsActive)
        {
            return;
        }

        WriteFixed(1, _headerText);
        WriteFixed(Layout.Height, _footerText);
    }

    /// <summary>
    /// Recomputes the region after a terminal resize and repaints the fixed
    /// rows. Phase 1 stub: resize handling lands in Phase 4.
    /// </summary>
    public void OnResize()
    {
        // Intentionally a no-op for Phase 1; Phase 4 polls Console.WindowHeight
        // from the input/steering loops and calls this to reset the region.
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

    private static bool TryGetWindowHeight(out int height)
    {
        try
        {
            height = Console.WindowHeight;
            return height > 0;
        }
        catch (IOException)
        {
            // No console attached (redirected/headless).
            height = -1;
            return false;
        }
    }

    private static void SetRegion(int top, int bottom) => Console.Write($"\x1b[{top};{bottom}r");

    private static void WriteFixed(int row, string text) => Console.Write($"\x1b[{row};1H\x1b[2K{text}");
}

/// <summary>
/// Pure, console-free layout decision for <see cref="ScreenRegionController"/>.
/// Separating it from console I/O makes the opt-in/redirect/height math unit
/// testable headlessly.
/// </summary>
internal readonly record struct ScreenRegionLayout(bool Active, int Height, int ScrollTop, int ScrollBottom)
{
    /// <summary>One fixed header row at the top.</summary>
    public const int HeaderRows = 1;

    /// <summary>Two fixed footer rows at the bottom (status + prompt).</summary>
    public const int FooterRows = 2;

    /// <summary>
    /// Smallest terminal that leaves at least three scrolling rows between the
    /// fixed header and footer.
    /// </summary>
    public const int MinimumHeight = HeaderRows + FooterRows + 3;

    /// <summary>
    /// Resolves the layout from the opt-in flag, redirected state, and terminal
    /// height. Returns an inactive layout when the feature is off, redirected,
    /// or the terminal is too short.
    /// </summary>
    public static ScreenRegionLayout Resolve(bool optedIn, bool redirected, int height)
    {
        if (!optedIn || redirected || height < MinimumHeight)
        {
            return default;
        }

        int top = HeaderRows + 1; // header occupies row 1; region starts at row 2.
        int bottom = height - FooterRows; // leave the last two rows for the footer.
        return new ScreenRegionLayout(true, height, top, bottom);
    }
}
