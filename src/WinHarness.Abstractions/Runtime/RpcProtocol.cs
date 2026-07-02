using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Runtime;

/// <summary>
/// One request on the RPC stdin stream. LF-delimited single-line JSON.
/// </summary>
public sealed record RpcRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("providerId")] string? ProviderId = null,
    [property: JsonPropertyName("modelId")] string? ModelId = null,
    [property: JsonPropertyName("session")] string? Session = null,
    [property: JsonPropertyName("name")] string? Name = null);

/// <summary>
/// One response on the RPC stdout stream, correlated by request id. Turn
/// events (JsonChatEvent) are interleaved on the same stream carrying the
/// initiating request's id in <see cref="RequestId"/>.
/// </summary>
public sealed record RpcResponse(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("ok")] bool? Ok = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("result")] JsonElement? Result = null)
{
    /// <summary>Success response.</summary>
    public static RpcResponse Success(string id, JsonElement? result = null) =>
        new("response", id, Ok: true, Result: result);

    /// <summary>Failure response.</summary>
    public static RpcResponse Failure(string id, string error) =>
        new("response", id, Ok: false, Error: error);
}

/// <summary>
/// A turn event wrapped for the RPC stream, carrying the initiating
/// request id so clients can correlate concurrent output.
/// </summary>
public sealed record RpcEvent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("event")] JsonChatEvent Event)
{
    /// <summary>Wraps a chat event for the stream.</summary>
    public static RpcEvent For(string requestId, JsonChatEvent chatEvent) =>
        new("event", requestId, chatEvent);
}

/// <summary>
/// Source-generated contracts for the RPC protocol.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcEvent))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class RpcJsonContext : JsonSerializerContext;
