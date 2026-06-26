namespace WinHarness.Runtime;

/// <summary>
/// Runs an agent workflow and streams runtime events.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Runs an agent request.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Input for an agent runtime run.
/// </summary>
public sealed record AgentRunRequest(string ProviderId, string ModelId, string Prompt);

/// <summary>
/// A runtime event emitted during an agent run.
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string Message);

/// <summary>
/// Agent runtime event kinds.
/// </summary>
public enum AgentEventKind
{
    /// <summary>
    /// Assistant text delta.
    /// </summary>
    AssistantDelta,

    /// <summary>
    /// Tool activity update.
    /// </summary>
    ToolActivity,

    /// <summary>
    /// Run completion.
    /// </summary>
    Completed,

    /// <summary>
    /// Run failure.
    /// </summary>
    Failed
}
