using System.Runtime.InteropServices;

namespace WinHarness.Platform;

/// <summary>
/// ConPTY interactive command entry point.
/// </summary>
public static partial class ConPtyCommandExecutor
{
    /// <summary>
    /// Executes an interactive command path.
    /// </summary>
    public static ValueTask<CommandResult> ExecuteInteractiveAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(new CommandResult(
                ExitCode: 1,
                StandardOutput: string.Empty,
                StandardError: "ConPTY interactive execution is only available on Windows.",
                Mode: CommandExecutionMode.Interactive));
        }

        return ValueTask.FromResult(new CommandResult(
            ExitCode: 1,
            StandardOutput: string.Empty,
            StandardError: "ConPTY interop boundary is present; full screen-buffer pump is completed in the interactive tool phase.",
            Mode: CommandExecutionMode.Interactive));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CreatePseudoConsole(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int ResizePseudoConsole(IntPtr hPC, Coord size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void ClosePseudoConsole(IntPtr hPC);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public Coord(short x, short y)
        {
            X = x;
            Y = y;
        }

        public short X { get; }

        public short Y { get; }
    }
}
