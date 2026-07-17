using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using System.Text.RegularExpressions;
using WinHarness.Cli.Rendering;
using WinHarness.Runtime;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ToolBatchRendererTests
{
    private static readonly Regex AnsiEscapeSequence = new(
        @"\u001B(?:\[[0-?]*[ -/]*[@-~]|\][^\u0007]*(?:\u0007|\u001B\\))",
        RegexOptions.Compiled);

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

        StringAssert.Contains(PlainText(output), "2 tool runs · 1 ok · 0 failed · 1 running");
    }

    [TestMethod]
    public void InterimSettlementMarksUnfinishedToolAsRunning()
    {
        using StringWriter output = new();
        ToolBatchRenderer renderer = new(verbose: false, console: CreateConsole(output));
        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));

        renderer.Settle();

        string rendered = PlainText(output);
        StringAssert.Contains(rendered, "tool run · 0 ok · 0 failed · 1 running");
        Assert.IsFalse(rendered.Contains("0 ok · 1 failed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TerminalSettlementMarksUnfinishedToolAsInterrupted()
    {
        using StringWriter output = new();
        ToolBatchRenderer renderer = new(verbose: false, console: CreateConsole(output));
        renderer.OnEvent(new ToolActivityInfo("run_command", ToolActivityPhase.Started));

        renderer.Settle(terminal: true);

        string rendered = PlainText(output);
        StringAssert.Contains(rendered, "tool run · 0 ok · 0 failed · 1 interrupted");
        Assert.IsFalse(rendered.Contains("0 ok · 1 failed", StringComparison.Ordinal));
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

    private static string PlainText(StringWriter output)
    {
        return AnsiEscapeSequence.Replace(output.ToString(), string.Empty);
    }
}
