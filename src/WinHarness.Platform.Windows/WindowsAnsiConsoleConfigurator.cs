using System.Runtime.InteropServices;
using System.Text;

namespace WinHarness.Platform;

/// <summary>
/// Enables Windows virtual terminal processing.
/// </summary>
public sealed partial class WindowsAnsiConsoleConfigurator : IAnsiConsoleConfigurator
{
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <inheritdoc />
    public bool IsVirtualTerminalEnabled
    {
        get
        {
            // Non-Windows terminals are xterm-compatible and process DECSTBM
            // natively; redirected output is filtered by the controller itself.
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            IntPtr handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return false;
            }

            if (!GetConsoleMode(handle, out uint mode))
            {
                return false;
            }

            // EnableAnsi() tried to set this flag at startup; if the terminal
            // accepted it, DECSTBM scroll regions are honored.
            return (mode & EnableVirtualTerminalProcessing) != 0;
        }
    }

    /// <inheritdoc />
    public void EnableAnsi()
    {
        // Ensure Unicode (emoji, box-drawing, etc.) renders instead of "??".
        // The legacy OEM code page mangles non-ASCII output regardless of VT processing.
        TryEnableUtf8();

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

    private static void TryEnableUtf8()
    {
        try
        {
            UTF8Encoding utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8NoBom;
            if (!Console.IsInputRedirected)
            {
                Console.InputEncoding = utf8NoBom;
            }
        }
        catch (IOException)
        {
            // Output redirected to a non-console stream; encoding stays as-is.
        }
        catch (PlatformNotSupportedException)
        {
            // Encoding override unavailable on this host.
        }
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
