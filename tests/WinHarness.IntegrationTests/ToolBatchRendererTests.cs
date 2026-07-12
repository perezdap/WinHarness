using Microsoft.VisualStudio.TestTools.UnitTesting;
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
}
