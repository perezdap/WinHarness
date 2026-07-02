using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Runtime;

/// <summary>
/// One JSONL record on the machine-readable event stream (chat --output json).
/// The <c>type</c> discriminator is stable contract; see docs/design/json-events.md.
/// </summary>
public sealed record JsonChatEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("toolName")] string? ToolName = null,
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("succeeded")] bool? Succeeded = null,
    [property: JsonPropertyName("durationMs")] double? DurationMs = null,
    [property: JsonPropertyName("inputTokens")] long? InputTokens = null,
    [property: JsonPropertyName("outputTokens")] long? OutputTokens = null,
    [property: JsonPropertyName("providerId")] string? ProviderId = null,
    [property: JsonPropertyName("modelId")] string? ModelId = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    /// <summary>Turn started.</summary>
    public static JsonChatEvent TurnStart(string providerId, string modelId) =>
        new("turn_start", ProviderId: providerId, ModelId: modelId);

    /// <summary>Streaming assistant text delta.</summary>
    public static JsonChatEvent AssistantDelta(string text) =>
        new("assistant_delta", Text: text);

    /// <summary>Tool call phase change.</summary>
    public static JsonChatEvent Tool(ToolActivityInfo info) =>
        new(
            "tool",
            ToolName: info.ToolName,
            Phase: info.Phase switch
            {
                ToolActivityPhase.Started => "started",
                ToolActivityPhase.Completed => "completed",
                ToolActivityPhase.Failed => "failed",
                _ => "unknown"
            },
            Succeeded: info.Succeeded,
            DurationMs: info.Duration?.TotalMilliseconds);

    /// <summary>Final assistant message for the turn.</summary>
    public static JsonChatEvent AssistantMessage(string text) =>
        new("assistant_message", Text: text);

    /// <summary>Token usage for the turn.</summary>
    public static JsonChatEvent Usage(long? inputTokens, long? outputTokens) =>
        new("usage", InputTokens: inputTokens, OutputTokens: outputTokens);

    /// <summary>Turn finished (terminal on success).</summary>
    public static JsonChatEvent TurnEnd() => new("turn_end");

    /// <summary>Turn failed (terminal on failure).</summary>
    public static JsonChatEvent FromError(string message) => new("error", Error: message);
}

/// <summary>
/// Source-generated contracts for the JSON event stream.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonChatEvent))]
public sealed partial class JsonChatEventContext : JsonSerializerContext;
