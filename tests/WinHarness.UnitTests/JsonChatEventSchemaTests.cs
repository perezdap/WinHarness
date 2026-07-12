using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Runtime;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class JsonChatEventSchemaTests
{
    [TestMethod]
    public void ToolEventsDoNotExposeDisplayLabels()
    {
        JsonChatEvent chatEvent = JsonChatEvent.Tool(new ToolActivityInfo(
            "run_command",
            ToolActivityPhase.Completed,
            Succeeded: true,
            Duration: TimeSpan.FromMilliseconds(42),
            DisplayLabel: "run_command --token secret"));

        string json = JsonSerializer.Serialize(chatEvent, JsonChatEventContext.Default.JsonChatEvent);

        Assert.IsFalse(json.Contains("displayLabel", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("secret", StringComparison.Ordinal));
    }
}
