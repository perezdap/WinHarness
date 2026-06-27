using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Serialization;
using WinHarness.Sessions;
using WinHarness.Tools;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class WinHarnessJsonSerializerContextTests
{
    [TestMethod]
    public void SerializesConfigurationWithSourceGeneratedContext()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "coder"
        };
        ProviderOptions provider = new()
        {
            Id = "local",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1"
        };
        provider.Models.Add(new ModelOptions
        {
            Id = "coder",
            ProviderModelId = "qwen",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: false,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: false,
                Reasoning: false)
        });
        options.Providers.Add(provider);

        string json = JsonSerializer.Serialize(options, WinHarnessJsonSerializerContext.Default.WinHarnessOptions);

        StringAssert.Contains(json, "\"defaultProvider\":\"local\"");
        StringAssert.Contains(json, "\"providerModelId\":\"qwen\"");
    }

    [TestMethod]
    public void SerializesToolAndCommandDtosWithSourceGeneratedContext()
    {
        ToolResult toolResult = new(
            Succeeded: true,
            Content: "ok",
            Metadata: new Dictionary<string, string>
            {
                ["command.mode"] = "Captured"
            });
        CommandResult commandResult = new(
            ExitCode: 0,
            StandardOutput: "stdout",
            StandardError: string.Empty,
            Mode: CommandExecutionMode.Captured);

        string toolJson = JsonSerializer.Serialize(toolResult, WinHarnessJsonSerializerContext.Default.ToolResult);
        string commandJson = JsonSerializer.Serialize(commandResult, WinHarnessJsonSerializerContext.Default.CommandResult);

        StringAssert.Contains(toolJson, "\"command.mode\":\"Captured\"");
        StringAssert.Contains(commandJson, "\"mode\":0");
    }

    [TestMethod]
    public void SerializesConversationMessagesWithContentBlocks()
    {
        ConversationMessage message = new(
            ConversationRole.Assistant,
            [
                ContentBlock.CreateText("I'll read the file."),
                ContentBlock.CreateToolCall("call_01", "read_file", """{"path":"README.md"}""")
            ],
            ProviderId: "openai-main",
            ModelId: "gpt-primary",
            Usage: new MessageUsage(1200, 340, 1540));

        string json = JsonSerializer.Serialize(message, WinHarnessJsonSerializerContext.Default.ConversationMessage);

        StringAssert.Contains(json, "\"kind\":1");
        StringAssert.Contains(json, "\"toolCallId\":\"call_01\"");
        StringAssert.Contains(json, "\"inputTokens\":1200");
    }

    [TestMethod]
    public void SerializesTurnArtifactsWithSourceGeneratedContext()
    {
        TurnArtifacts artifacts = new(
        [
            ConversationMessage.FromText(ConversationRole.User, "hello"),
            ConversationMessage.FromText(ConversationRole.Assistant, "hi")
        ]);

        string json = JsonSerializer.Serialize(artifacts, WinHarnessJsonSerializerContext.Default.TurnArtifacts);

        StringAssert.Contains(json, "\"messages\"");
        StringAssert.Contains(json, "\"text\":\"hello\"");
    }

    [TestMethod]
    public void SerializesSessionEntriesWithSourceGeneratedContext()
    {
        CompactionSessionEntry compaction = new(
            "f6g7h8i9",
            "e5f6g7h8",
            DateTimeOffset.Parse("2026-06-27T14:10:00.000Z"),
            "summary",
            "c3d4e5f6",
            42000);
        ModelChangeSessionEntry modelChange = new(
            "d4e5f6g7",
            "c3d4e5f6",
            DateTimeOffset.Parse("2026-06-27T14:05:00.000Z"),
            "local-ollama",
            "local-coder");
        SessionInfoSessionEntry sessionInfo = new(
            "k1l2m3n4",
            "j0k1l2m3",
            DateTimeOffset.Parse("2026-06-27T14:35:00.000Z"),
            "Phase 1");

        string compactionJson = JsonSerializer.Serialize(
            (SessionEntry)compaction,
            WinHarnessJsonSerializerContext.Default.SessionEntry);
        string modelChangeJson = JsonSerializer.Serialize(
            (SessionEntry)modelChange,
            WinHarnessJsonSerializerContext.Default.SessionEntry);
        string sessionInfoJson = JsonSerializer.Serialize(
            (SessionEntry)sessionInfo,
            WinHarnessJsonSerializerContext.Default.SessionEntry);

        StringAssert.Contains(compactionJson, "\"type\":\"compaction\"");
        StringAssert.Contains(modelChangeJson, "\"type\":\"model_change\"");
        StringAssert.Contains(sessionInfoJson, "\"type\":\"session_info\"");
    }
}
