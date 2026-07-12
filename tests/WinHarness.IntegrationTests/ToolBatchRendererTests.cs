using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using WinHarness.Cli.Rendering;
using WinHarness.Runtime;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ToolBatchRendererTests
{
    [TestMethod]
    public void StartedToolUpdatesTheCompactSpinnerLabel()
    {
        ToolBatchRenderer renderer = new(verbose: false);

        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));

        Assert.IsTrue(renderer.HasPendingBatch);
        Assert.AreEqual("running 1 tool", renderer.LiveLabel);
    }

    [TestMethod]
    public void CompletedToolsRemainGroupedUntilSettlement()
    {
        ToolBatchRenderer renderer = new(verbose: false);
        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));
        renderer.OnEvent(new ToolActivityInfo(
            "run_command",
            ToolActivityPhase.Completed,
            Succeeded: true,
            Duration: TimeSpan.FromMilliseconds(42)));

        Assert.IsTrue(renderer.HasPendingBatch);
        Assert.AreEqual("tool activity · 1 ok · 0 failed", renderer.LiveLabel);

        renderer.Settle();

        Assert.IsFalse(renderer.HasPendingBatch);
        Assert.AreEqual("thinking", renderer.LiveLabel);
    }

    [TestMethod]
    public void SettlementCountsCompletedAndUnfinishedTools()
    {
        using StringWriter output = new();
        ToolBatchRenderer renderer = new(verbose: false, console: CreateConsole(output));
        renderer.OnEvent(new ToolActivityInfo("read_file", ToolActivityPhase.Started));
        renderer.OnEvent(new ToolActivityInfo(
            "read_file",
            ToolActivityPhase.Completed,
            Succeeded: true,
            Duration: TimeSpan.FromMilliseconds(42)));
        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));

        renderer.Settle();

        StringAssert.Contains(output.ToString(), "2 tool runs · 1 ok · 1 failed");
    }

    [TestMethod]
    public void SettlementMarksUnfinishedToolAsFailed()
    {
        using StringWriter output = new();
        ToolBatchRenderer renderer = new(verbose: false, console: CreateConsole(output));
        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));

        renderer.Settle();

        StringAssert.Contains(output.ToString(), "tool run · 0 ok · 1 failed");
        Assert.IsFalse(output.ToString().Contains("0 ok · 0 failed", StringComparison.Ordinal));
    }

    private static IAnsiConsole CreateConsole(TextWriter output)
    {
        return AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output)
        });
    }
}
