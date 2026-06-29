using System.Text.Json;
using System.Text.Json.Serialization;
using WinHarness.Configuration;
using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Sessions;
using WinHarness.Tools;

namespace WinHarness.Serialization;

/// <summary>
/// Source-generated JSON contracts for WinHarness-owned DTOs.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WinHarnessOptions))]
[JsonSerializable(typeof(ProviderOptions))]
[JsonSerializable(typeof(ModelOptions))]
[JsonSerializable(typeof(McpServerOptions))]
[JsonSerializable(typeof(ProviderCapabilities))]
[JsonSerializable(typeof(global::WinHarness.Conversation.Conversation))]
[JsonSerializable(typeof(ConversationMessage))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(ContentBlockKind))]
[JsonSerializable(typeof(MessageUsage))]
[JsonSerializable(typeof(ContentBlock[]))]
[JsonSerializable(typeof(ConversationMessage[]))]
[JsonSerializable(typeof(DiagnosticRecord))]
[JsonSerializable(typeof(ProjectContext))]
[JsonSerializable(typeof(AgentRunRequest))]
[JsonSerializable(typeof(AgentEvent))]
[JsonSerializable(typeof(ToolActivityInfo))]
[JsonSerializable(typeof(ToolActivityPhase))]
[JsonSerializable(typeof(TurnArtifacts))]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(SessionEntry))]
[JsonSerializable(typeof(MessageSessionEntry))]
[JsonSerializable(typeof(CompactionSessionEntry))]
[JsonSerializable(typeof(ModelChangeSessionEntry))]
[JsonSerializable(typeof(SessionInfoSessionEntry))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(SessionEntry[]))]
[JsonSerializable(typeof(ToolInvocation))]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public sealed partial class WinHarnessJsonSerializerContext : JsonSerializerContext;
