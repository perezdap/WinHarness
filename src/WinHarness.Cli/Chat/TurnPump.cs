using WinHarness.Conversation;
using WinHarness.Runtime;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Consumes one turn's <see cref="AgentEvent"/> stream: invokes a presentation
/// callback per event in stream order, appends the turn artifacts carried by
/// the terminal event to the session, and captures the failure message and
/// token usage. The front-ends (text REPL, JSON event stream, RPC host) plug
/// in as presentation adapters; this module owns everything else about reading
/// a turn.
/// </summary>
/// <remarks>
/// Terminal-event protocol, emitted by <c>SingleAgentRuntime</c> and
/// interpreted only here:
/// <list type="bullet">
/// <item><see cref="AgentEventKind.Failed"/> — the turn failed; the event
/// message is the failure reason. It may be followed by a partial
/// completion.</item>
/// <item><see cref="AgentEventKind.Completed"/> with
/// <see cref="PartialCompletionMessage"/> — follows a failure when assistant
/// text was streamed before the stop; carries the truncated turn artifacts
/// (the failed user message plus the partial assistant text) so partial work
/// is appended, not lost.</item>
/// <item><see cref="AgentEventKind.Completed"/> with
/// <see cref="NormalCompletionMessage"/> — success; carries the full turn
/// artifacts (user input, assistant segments, tool results).</item>
/// </list>
/// Cancellation is not interpreted here: <see cref="OperationCanceledException"/>
/// from the runtime propagates to the caller unchanged.
/// </remarks>
internal sealed class TurnPump
{
    /// <summary>
    /// <see cref="AgentEvent.Message"/> carried by a normal (successful)
    /// terminal <see cref="AgentEventKind.Completed"/> event.
    /// </summary>
    internal const string NormalCompletionMessage = "completed";

    /// <summary>
    /// <see cref="AgentEvent.Message"/> carried by the terminal
    /// <see cref="AgentEventKind.Completed"/> event that follows a
    /// <see cref="AgentEventKind.Failed"/> event when partial assistant text
    /// was streamed; its artifacts are the truncated turn artifacts.
    /// </summary>
    internal const string PartialCompletionMessage = "partial";

    private readonly ChatSession _session;

    public TurnPump(ChatSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Runs one turn: streams events from <paramref name="runtime"/>, appends
    /// the artifacts carried by a terminal <see cref="AgentEventKind.Completed"/>
    /// event (full artifacts on success, partial artifacts after a failure),
    /// and invokes <paramref name="present"/> once per event in stream order,
    /// at exactly the point the event is read. Returns what the turn produced;
    /// whatever the runtime throws (including
    /// <see cref="OperationCanceledException"/>) propagates unchanged.
    /// </summary>
    public async ValueTask<TurnOutcome> RunAsync(
        AgentRunRequest request,
        IAgentRuntime runtime,
        Func<AgentEvent, ValueTask> present,
        CancellationToken cancellationToken)
    {
        string? failureMessage = null;
        MessageUsage? usage = null;
        TurnArtifacts? appendedArtifacts = null;
        bool appendedPartialArtifacts = false;

        await foreach (AgentEvent agentEvent in runtime.RunAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (agentEvent.Kind)
            {
                case AgentEventKind.Failed:
                    failureMessage = agentEvent.Message;
                    break;

                case AgentEventKind.Completed when agentEvent.TurnArtifacts is { } artifacts:
                    await _session.AppendTurnAsync(artifacts, cancellationToken).ConfigureAwait(false);
                    appendedArtifacts = artifacts;
                    appendedPartialArtifacts = string.Equals(
                        agentEvent.Message,
                        PartialCompletionMessage,
                        StringComparison.Ordinal);
                    usage = artifacts.Messages
                        .LastOrDefault(static message => message.Role == ConversationRole.Assistant)
                        ?.Usage;
                    break;
            }

            await present(agentEvent).ConfigureAwait(false);
        }

        return new TurnOutcome(failureMessage, usage, appendedArtifacts, appendedPartialArtifacts);
    }

    /// <summary>
    /// The post-turn steering policy: steering that never found a tool-round
    /// injection point must not be lost — it is promoted to follow-up input so
    /// it still runs. Front-ends with a follow-up concept (the REPL) call this
    /// when a turn ends and when an aborted turn is torn down; front-ends
    /// without one (RPC) leave unconsumed steering queued for the next turn.
    /// </summary>
    internal static void PromoteUnconsumedSteering(SteeringQueue steering, Queue<string> followUps)
    {
        foreach (string queued in steering.DrainAll())
        {
            followUps.Enqueue(queued);
        }
    }
}

/// <summary>
/// What one pumped turn produced: the failure reason when the runtime reported
/// <see cref="AgentEventKind.Failed"/> (null on success), the token usage from
/// the last assistant message of the appended artifacts, and the artifacts
/// appended to the session (null when the turn produced none).
/// <see cref="AppendedPartialArtifacts"/> distinguishes the truncated
/// artifacts of a <see cref="TurnPump.PartialCompletionMessage"/> completion
/// from the full artifacts of a normal one.
/// </summary>
internal sealed record TurnOutcome(
    string? FailureMessage,
    MessageUsage? Usage,
    TurnArtifacts? AppendedArtifacts,
    bool AppendedPartialArtifacts);
