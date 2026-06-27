namespace WinHarness.Conversation;

/// <summary>
/// Content block kinds for conversation messages.
/// </summary>
public enum ContentBlockKind
{
    /// <summary>
    /// Plain text.
    /// </summary>
    Text,

    /// <summary>
    /// A tool invocation requested by the assistant.
    /// </summary>
    ToolCall,

    /// <summary>
    /// A tool execution result.
    /// </summary>
    ToolResult
}

/// <summary>
/// A typed content block within a conversation message.
/// </summary>
public sealed record ContentBlock(
    ContentBlockKind Kind,
    string? Text = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ArgumentsJson = null,
    bool IsError = false)
{
    /// <summary>
    /// Creates a text block.
    /// </summary>
    public static ContentBlock CreateText(string text) => new(ContentBlockKind.Text, Text: text);

    /// <summary>
    /// Creates a tool call block.
    /// </summary>
    public static ContentBlock CreateToolCall(string id, string name, string argumentsJson) =>
        new(ContentBlockKind.ToolCall, ToolCallId: id, ToolName: name, ArgumentsJson: argumentsJson);

    /// <summary>
    /// Creates a tool result block.
    /// </summary>
    public static ContentBlock CreateToolResult(
        string toolCallId,
        string toolName,
        string text,
        bool isError = false) =>
        new(ContentBlockKind.ToolResult, Text: text, ToolCallId: toolCallId, ToolName: toolName, IsError: isError);
}