using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Platform;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class CommandExecutorTests
{
    [TestMethod]
    public async Task CapturedExecutorReturnsCleanOutput()
    {
        CapturedCommandExecutor executor = new();
        CommandRequest request = CreateEchoRequest(CommandExecutionMode.Captured);

        CommandResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StandardOutput, "winharness-command");
        Assert.AreEqual(CommandExecutionMode.Captured, result.Mode);
    }

    [TestMethod]
    public async Task CapturedExecutorKillsTimedOutProcess()
    {
        CapturedCommandExecutor executor = new();
        CommandRequest request = OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", ["/c", "ping 127.0.0.1 -n 6 >NUL"], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromMilliseconds(100))
            : new CommandRequest("/bin/sh", ["-c", "sleep 5"], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromMilliseconds(100));

        CommandResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.StandardError, "Process timed out.");
        Assert.AreEqual(CommandExecutionMode.Captured, result.Mode);
    }

    [TestMethod]
    public async Task CapturedExecutorReturnsNonZeroExitCode()
    {
        CapturedCommandExecutor executor = new();
        CommandRequest request = OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", ["/c", "exit /b 7"], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(10))
            : new CommandRequest("/bin/sh", ["-c", "exit 7"], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(10));

        CommandResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(7, result.ExitCode);
        Assert.AreEqual(CommandExecutionMode.Captured, result.Mode);
    }

    [TestMethod]
    public async Task CapturedExecutorDoesNotHangOnCommandsThatReadStdin()
    {
        CapturedCommandExecutor executor = new();

        // A command that reads from stdin previously inherited the harness console
        // and blocked forever (surfacing as a tool timeout). Captured mode now closes
        // stdin so the child receives EOF and completes promptly.
        CommandRequest request = OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", ["/c", "set /p value=Enter: "], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(10))
            : new CommandRequest("/bin/sh", ["-c", "read value"], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(10));

        CommandResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        // The important guarantee is that it returns (does not time out) well within the
        // 10s budget; the exact exit code from an EOF read is shell-specific.
        Assert.AreEqual(CommandExecutionMode.Captured, result.Mode);
        StringAssert.DoesNotMatch(result.StandardError, new System.Text.RegularExpressions.Regex("Process timed out\\."));
    }

    [TestMethod]
    public async Task InteractiveExecutorUsesConPtyOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ConPTY requires Windows.");
        }

        CapturedCommandExecutor executor = new();
        CommandRequest request = CreateEchoRequest(CommandExecutionMode.Interactive);

        CommandResult result = await executor.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(CommandExecutionMode.Interactive, result.Mode);
    }

    private static CommandRequest CreateEchoRequest(CommandExecutionMode mode)
    {
        return OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", ["/c", "echo winharness-command"], Environment.CurrentDirectory, mode, TimeSpan.FromSeconds(10))
            : new CommandRequest("/bin/sh", ["-c", "echo winharness-command"], Environment.CurrentDirectory, mode, TimeSpan.FromSeconds(10));
    }
}
