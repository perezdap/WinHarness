using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Tools;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ToolActivitySummarizerTests
{
    [TestMethod]
    public void BuildIncludesStructuredFilePath()
    {
        string? label = ToolActivitySummarizer.Build(
            "read_file",
            """{"path":"src/WinHarness.Cli/Program.cs"}""");

        Assert.AreEqual("read_file src/WinHarness.Cli/Program.cs", label);
    }

    [TestMethod]
    public void BuildDoesNotExposeRunCommandArguments()
    {
        string? label = ToolActivitySummarizer.Build(
            "run_command",
            """{"command":"curl -H \"Authorization: Bearer sk-live-token\" --token super-secret"}""");

        Assert.AreEqual("run_command", label);
    }

    [TestMethod]
    public void BuildDoesNotExposeSearchArguments()
    {
        string? label = ToolActivitySummarizer.Build(
            "grep",
            """{"query":"super-secret","path":"src"}""");

        Assert.AreEqual("grep", label);
    }

    [TestMethod]
    public void BuildFallsBackToToolNameForMalformedArguments()
    {
        string? label = ToolActivitySummarizer.Build("read_file", "not-json");

        Assert.AreEqual("read_file", label);
    }
}
