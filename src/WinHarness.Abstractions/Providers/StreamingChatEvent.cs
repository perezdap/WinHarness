namespace WinHarness.Providers;

/// <summary>
/// Provider-neutral streaming event.
/// </summary>
public sealed record StreamingChatEvent(StreamingChatEventKind Kind, string Content);

/// <summary>
/// Streaming chat event kinds.
/// </summary>
public enum StreamingChatEventKind
{
    /// <summary>
    /// Assistant text delta.
    /// </summary>
    TextDelta,

    /// <summary>
    /// Reasoning text delta.
    /// </summary>
    ReasoningDelta,

    /// <summary>
    /// Tool call requested by the model.
    /// </summary>
    ToolCallRequested,

    /// <summary>
    /// Tool result supplied to the model.
    /// </summary>
    ToolResult,

    /// <summary>
    /// Token or usage information.
    /// </summary>
    Usage,

    /// <summary>
    /// Streaming completed.
    /// </summary>
    Completed,

    /// <summary>
    /// Streaming failed.
    /// </summary>
    Failed
}
