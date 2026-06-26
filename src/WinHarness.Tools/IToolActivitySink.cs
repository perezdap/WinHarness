namespace WinHarness.Tools;

/// <summary>
/// Receives tool activity notifications.
/// </summary>
public interface IToolActivitySink
{
    /// <summary>
    /// Records that a tool started.
    /// </summary>
    void ToolStarted(string toolName);

    /// <summary>
    /// Records that a tool completed.
    /// </summary>
    void ToolCompleted(string toolName, ToolResult result, TimeSpan duration);

    /// <summary>
    /// Records that a tool threw an exception.
    /// </summary>
    void ToolFailed(string toolName, Exception exception, TimeSpan duration);
}
