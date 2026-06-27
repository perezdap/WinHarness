using ConversationState = WinHarness.Conversation.Conversation;
using WinHarness.Context;
using WinHarness.Conversation;

namespace WinHarness.Runtime;

/// <summary>
/// Runs an agent workflow and streams runtime events.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Runs one turn against the conversation and streams runtime events.
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Input for one turn. The conversation must end with a user message; the
/// runtime never mutates it. On completion the caller appends the turn
/// artifacts carried by the terminal <see cref="AgentEventKind.Completed"/> event.
/// </summary>
public sealed record AgentRunRequest(
    string ProviderId,
    string ModelId,
    ConversationState Conversation,
    string WorkspaceRoot = "",
    ProjectContext? ProjectContext = null);

/// <summary>
/// A runtime event emitted during an agent run. The terminal
/// <see cref="AgentEventKind.Completed"/> event carries
/// <see cref="TurnArtifacts"/> for the caller to append to its conversation.
/// </summary>
public sealed record AgentEvent(AgentEventKind Kind, string Message, TurnArtifacts? TurnArtifacts = null);

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