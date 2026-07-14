using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Tools;
using WinHarness.Tools.Docs;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class WinHarnessDocsTests
{
    [TestMethod]
    public void CatalogListsAllEmbeddedTopics()
    {
        IReadOnlyList<WinHarnessDocsCatalog.DocTopic> topics = WinHarnessDocsCatalog.ListTopics();

        CollectionAssert.AreEquivalent(
            new[] { "paths", "sessions", "tools", "providers", "mcp", "chat", "diagnostics" },
            topics.Select(static topic => topic.Id).ToArray());
    }

    [TestMethod]
    public async Task ToolListsCatalogWhenTopicOmitted()
    {
        WinHarnessDocsTool tool = new();
        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("winharness_docs", Json("{}")),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "sessions");
        StringAssert.Contains(result.Content, "paths");
    }

    [TestMethod]
    public async Task ToolReturnsSessionsPath()
    {
        WinHarnessDocsTool tool = new();
        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("winharness_docs", Json("""{"topic":"sessions"}""")),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "%APPDATA%\\WinHarness\\sessions");
    }

    [TestMethod]
    public async Task ToolFiltersLinesByQuery()
    {
        WinHarnessDocsTool tool = new();
        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("winharness_docs", Json("""{"topic":"chat","query":"/compact"}""")),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "/compact");
        StringAssert.DoesNotMatch(result.Content, new System.Text.RegularExpressions.Regex("/providers"));
    }

    [TestMethod]
    public async Task ToolReturnsUnknownTopicError()
    {
        WinHarnessDocsTool tool = new();
        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("winharness_docs", Json("""{"topic":"nope"}""")),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("unknown_topic", result.ErrorCode);
        StringAssert.Contains(result.Content, "sessions");
    }

    [TestMethod]
    public void LookupLoadsEveryRegisteredTopic()
    {
        foreach (WinHarnessDocsCatalog.DocTopic topic in WinHarnessDocsCatalog.ListTopics())
        {
            WinHarnessDocsCatalog.DocLookupResult result = WinHarnessDocsCatalog.Lookup(topic.Id, query: null, maxOutputBytes: 32 * 1024);
            Assert.IsTrue(result.Succeeded, topic.Id);
            StringAssert.Contains(result.Content, "#");
        }
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
