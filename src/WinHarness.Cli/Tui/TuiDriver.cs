using Terminal.Gui.Drivers;

namespace WinHarness.Cli.Tui;

/// <summary>
/// Terminal.Gui driver selection for WinHarness TUI entry points.
/// </summary>
internal static class TuiDriver
{
    /// <summary>
    /// Configure Terminal.Gui before <see cref="Terminal.Gui.App.IApplication.Init"/>.
    /// </summary>
    public static void Configure()
    {
        // The ANSI driver's default size detection sends CSI 18t queries. When the
        // ESC [ 8 ; height ; width t response is not consumed, it appears as garbage
        // like ^[[8;48;165t in Windows Terminal.
        if (ResolveName() == DriverRegistry.Names.ANSI)
        {
            Driver.SizeDetection = SizeDetectionMode.Polling;
        }
    }

    /// <summary>
    /// Pick a platform-appropriate driver name.
    /// </summary>
    public static string ResolveName()
        => OperatingSystem.IsWindows()
            ? DriverRegistry.Names.WINDOWS
            : DriverRegistry.Names.ANSI;
}
