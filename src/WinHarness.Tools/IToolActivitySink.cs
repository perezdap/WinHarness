namespace WinHarness.Tools;

/// <summary>
/// Receives tool activity notifications.
/// </summary>
public interface IToolActivitySink
{
    /// <summary>
    /// Records that a tool started. <paramref name="displayLabel"/> is a short,
    /// safe-to-print summary of the invocation (for example, "read_file
    /// README.md"); it may be <c>null</c> when the arguments are not available
    /// or when no per-tool summarizer is registered. Labels must not expose
    /// arbitrary command/search text or secrets.
    /// </summary>
    void ToolStarted(string toolName, string? displayLabel);

    /// <summary>
    /// Records that a tool completed.
    /// </summary>
    void ToolCompleted(string toolName, string? displayLabel, ToolResult result, TimeSpan duration);

    /// <summary>
    /// Records that a tool threw an exception.
    /// </summary>
    void ToolFailed(string toolName, string? displayLabel, Exception exception, TimeSpan duration);
}
