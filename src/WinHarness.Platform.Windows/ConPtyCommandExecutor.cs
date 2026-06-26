using System.ComponentModel;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinHarness.Platform;

/// <summary>
/// ConPTY interactive command entry point.
/// </summary>
public static partial class ConPtyCommandExecutor
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ProcThreadAttributePseudoConsole = 0x00020016;

    /// <summary>
    /// Executes an interactive command path.
    /// </summary>
    public static async ValueTask<CommandResult> ExecuteInteractiveAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CommandResult(
                ExitCode: 1,
                StandardOutput: string.Empty,
                StandardError: "ConPTY interactive execution is only available on Windows.",
                Mode: CommandExecutionMode.Interactive);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!CreatePipe(out SafeFileHandle inputReadSide, out SafeFileHandle inputWriteSide, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed for ConPTY input.");
        }

        if (!CreatePipe(out SafeFileHandle outputReadSide, out SafeFileHandle outputWriteSide, IntPtr.Zero, 0))
        {
            inputReadSide.Dispose();
            inputWriteSide.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreatePipe failed for ConPTY output.");
        }

        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        ProcessInformation processInformation = default;

        try
        {
            int hr = CreatePseudoConsole(
                new Coord(120, 30),
                inputReadSide.DangerousGetHandle(),
                outputWriteSide.DangerousGetHandle(),
                0,
                out pseudoConsole);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            inputReadSide.Dispose();
            outputWriteSide.Dispose();

            attributeList = CreatePseudoConsoleAttributeList(pseudoConsole);
            StartupInfoEx startupInfo = new()
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>()
                },
                LpAttributeList = attributeList
            };

            string commandLine = BuildCommandLine(request.FileName, request.Arguments);
            bool created = CreateProcessWithCommandLine(
                commandLine,
                request.WorkingDirectory,
                ref startupInfo,
                out processInformation);

            if (!created)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateProcess failed for '{request.FileName}'.");
            }

            inputWriteSide.Dispose();

            await using FileStream output = new(outputReadSide, FileAccess.Read, bufferSize: 4096, isAsync: false);
            Task<string> outputTask = ReadAllOutputAsync(output, cancellationToken);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);

            uint waitMilliseconds = request.Timeout == Timeout.InfiniteTimeSpan
                ? Infinite
                : checked((uint)Math.Min(request.Timeout.TotalMilliseconds, uint.MaxValue - 1));
            uint waitResult = await Task.Run(() => WaitForSingleObject(processInformation.Process, waitMilliseconds), timeout.Token)
                .ConfigureAwait(false);

            if (waitResult == WaitTimeout)
            {
                _ = TerminateProcess(processInformation.Process, 1);
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
                return new CommandResult(1, await outputTask.ConfigureAwait(false), "Interactive command timed out.", CommandExecutionMode.Interactive);
            }

            if (waitResult != WaitObject0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "WaitForSingleObject failed for ConPTY child process.");
            }

            if (!GetExitCodeProcess(processInformation.Process, out uint exitCode))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetExitCodeProcess failed for ConPTY child process.");
            }

            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
            string capturedOutput = await outputTask.ConfigureAwait(false);

            return new CommandResult(checked((int)exitCode), capturedOutput, string.Empty, CommandExecutionMode.Interactive);
        }
        finally
        {
            if (processInformation.Thread != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.Thread);
            }

            if (processInformation.Process != IntPtr.Zero)
            {
                _ = CloseHandle(processInformation.Process);
            }

            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
            }

            inputReadSide.Dispose();
            inputWriteSide.Dispose();
            outputReadSide.Dispose();
            outputWriteSide.Dispose();
        }
    }

    private static IntPtr CreatePseudoConsoleAttributeList(IntPtr pseudoConsole)
    {
        nuint bytes = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        IntPtr attributeList = Marshal.AllocHGlobal(checked((int)bytes));

        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref bytes))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeProcThreadAttributeList failed.");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "UpdateProcThreadAttribute failed for ConPTY.");
            }

            return attributeList;
        }
        catch
        {
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static async Task<string> ReadAllOutputAsync(FileStream output, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] bytes = new byte[4096];

        while (true)
        {
            int read = await output.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(bytes, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        StringBuilder builder = new();
        AppendQuoted(builder, fileName);
        foreach (string argument in arguments)
        {
            builder.Append(' ');
            AppendQuoted(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string value)
    {
        if (value.Length == 0)
        {
            builder.Append("\"\"");
            return;
        }

        bool needsQuotes = value.AsSpan().IndexOfAny(' ', '\t', '"') >= 0;
        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('"');
    }

    private static unsafe bool CreateProcessWithCommandLine(
        string commandLine,
        string workingDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation)
    {
        char[] mutableCommandLine = string.Concat(commandLine, '\0').ToCharArray();
        fixed (char* commandLinePointer = mutableCommandLine)
        {
            return CreateProcess(
                null,
                commandLinePointer,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                ExtendedStartupInfoPresent,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out processInformation);
        }
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        int nSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nuint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        nuint attribute,
        IntPtr lpValue,
        nuint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;

        public IntPtr LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

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
