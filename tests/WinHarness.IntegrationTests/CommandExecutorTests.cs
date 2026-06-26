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
