using System.Runtime.InteropServices;

namespace WinHarness.Platform;

/// <summary>
/// Enables Windows virtual terminal processing.
/// </summary>
public sealed partial class WindowsAnsiConsoleConfigurator : IAnsiConsoleConfigurator
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <inheritdoc />
    public void EnableAnsi()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return;
        }

        if (!GetConsoleMode(handle, out uint mode))
        {
            return;
        }

        _ = SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
