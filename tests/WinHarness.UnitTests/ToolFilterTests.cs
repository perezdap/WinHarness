using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Tools;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ToolFilterTests
{
    [TestMethod]
    public void NoConstraintsEnablesEverything()
    {
        ToolFilter filter = new();

        Assert.IsTrue(filter.IsEnabled("read_file"));
        Assert.IsTrue(filter.IsEnabled("filesystem.list_dir"));
    }

    [TestMethod]
    public void DisableAllWinsOverAllowList()
    {
        ToolFilter filter = new(Allow: ["read_file"], DisableAll: true);

        Assert.IsFalse(filter.IsEnabled("read_file"));
    }

    [TestMethod]
    public void AllowListEnablesOnlyListedNames()
    {
        ToolFilter filter = new(Allow: ["read_file", "grep"]);

        Assert.IsTrue(filter.IsEnabled("read_file"));
        Assert.IsTrue(filter.IsEnabled("grep"));
        Assert.IsFalse(filter.IsEnabled("write_file"));
        Assert.IsFalse(filter.IsEnabled("run_command"));
    }

    [TestMethod]
    public void ExcludeListDisablesListedNames()
    {
        ToolFilter filter = new(Exclude: ["run_command", "write_file"]);

        Assert.IsFalse(filter.IsEnabled("run_command"));
        Assert.IsFalse(filter.IsEnabled("write_file"));
        Assert.IsTrue(filter.IsEnabled("read_file"));
    }

    [TestMethod]
    public void ExcludeAppliesWithinAllowList()
    {
        ToolFilter filter = new(Allow: ["read_file", "grep"], Exclude: ["grep"]);

        Assert.IsTrue(filter.IsEnabled("read_file"));
        Assert.IsFalse(filter.IsEnabled("grep"));
    }

    [TestMethod]
    public void MatchingIsCaseInsensitive()
    {
        ToolFilter filter = new(Allow: ["Read_File"], Exclude: ["GREP"]);

        Assert.IsTrue(filter.IsEnabled("read_file"));
        Assert.IsFalse(filter.IsEnabled("grep"));
    }

    [TestMethod]
    public void EmptyListsBehaveAsUnset()
    {
        ToolFilter filter = new(Allow: [], Exclude: []);

        Assert.IsTrue(filter.IsEnabled("anything"));
    }
}
