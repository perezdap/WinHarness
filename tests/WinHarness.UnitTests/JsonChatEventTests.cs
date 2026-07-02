using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Runtime;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class JsonChatEventTests
{
    private static string Serialize(JsonChatEvent chatEvent) =>
        JsonSerializer.Serialize(chatEvent, JsonChatEventContext.Default.JsonChatEvent);

    [TestMethod]
    public void TurnStartCarriesProviderAndModel()
    {
        string json = Serialize(JsonChatEvent.TurnStart("local", "coder"));

        StringAssert.Contains(json, "\"type\":\"turn_start\"");
        StringAssert.Contains(json, "\"providerId\":\"local\"");
        StringAssert.Contains(json, "\"modelId\":\"coder\"");
    }

    [TestMethod]
    public void NullFieldsAreOmitted()
    {
        string json = Serialize(JsonChatEvent.AssistantDelta("hi"));

        StringAssert.Contains(json, "\"type\":\"assistant_delta\"");
        StringAssert.Contains(json, "\"text\":\"hi\"");
        Assert.IsFalse(json.Contains("toolName", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("error", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToolEventMapsPhases()
    {
        string started = Serialize(JsonChatEvent.Tool(new ToolActivityInfo("grep", ToolActivityPhase.Started)));
        string completed = Serialize(JsonChatEvent.Tool(new ToolActivityInfo(
            "grep", ToolActivityPhase.Completed, Succeeded: true, Duration: TimeSpan.FromMilliseconds(42))));

        StringAssert.Contains(started, "\"phase\":\"started\"");
        StringAssert.Contains(completed, "\"phase\":\"completed\"");
        StringAssert.Contains(completed, "\"succeeded\":true");
        StringAssert.Contains(completed, "\"durationMs\":42");
    }

    [TestMethod]
    public void SerializedEventsAreSingleLine()
    {
        string json = Serialize(JsonChatEvent.FromError("boom\nline2"));

        Assert.IsFalse(json.TrimEnd().Contains('\n'), "JSONL events must not contain raw newlines");
        StringAssert.Contains(json, "\\n");
    }

    [TestMethod]
    public void UsageCarriesTokenCounts()
    {
        string json = Serialize(JsonChatEvent.Usage(120, 45));

        StringAssert.Contains(json, "\"inputTokens\":120");
        StringAssert.Contains(json, "\"outputTokens\":45");
    }
}
