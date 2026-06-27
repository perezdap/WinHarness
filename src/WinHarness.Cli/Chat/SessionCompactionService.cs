using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;
using WinHarness.Runtime;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Runs a summarization turn and appends a compaction entry to the session.
/// </summary>
internal sealed class SessionCompactionService
{
    public const int CompactionKeepMessageEntries = 4;

    private const string SummarizationSystemPrompt =
        "Summarize the conversation for continuation. Preserve file paths, decisions, and unfinished work.";

    private const string DefaultSummarizationUserPrompt =
        "Summarize the conversation above for continuation.";

    private readonly IAgentRuntime _runtime;

    public SessionCompactionService(IAgentRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<CompactionResult> CompactAsync(
        ISessionManager session,
        string providerId,
        string modelId,
        string? instructions,
        string? skillSystemPrompt,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (!session.IsPersisted)
        {
            return CompactionResult.Failed("/compact requires a persisted session.");
        }

        IReadOnlyList<SessionEntry> branch = session.GetActiveBranch();
        List<MessageSessionEntry> messageEntries = branch.OfType<MessageSessionEntry>().ToList();
        if (messageEntries.Count < CompactionKeepMessageEntries)
        {
            return CompactionResult.Failed(
                $"Nothing to compact — need at least {CompactionKeepMessageEntries} message entries on the active branch.");
        }

        ConversationState before = session.BuildConversation(skillSystemPrompt);
        int charsBefore = CountConversationChars(before);

        string summary = await RunSummarizationAsync(
            session,
            providerId,
            modelId,
            instructions,
            skillSystemPrompt,
            workspaceRoot,
            cancellationToken).ConfigureAwait(false);

        string firstKeptEntryId = messageEntries[^CompactionKeepMessageEntries].Id;
        long? tokensBefore = FindLastUsageTokens(branch);

        await session.AppendCompactionAsync(summary, firstKeptEntryId, tokensBefore, cancellationToken)
            .ConfigureAwait(false);

        ConversationState after = session.BuildConversation(skillSystemPrompt);
        int charsAfter = CountConversationChars(after);

        return CompactionResult.Completed(charsBefore, charsAfter);
    }

    private async ValueTask<string> RunSummarizationAsync(
        ISessionManager session,
        string providerId,
        string modelId,
        string? instructions,
        string? skillSystemPrompt,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ConversationState branchConversation = session.BuildConversation(skillSystemPrompt);
        ConversationState summarizeConversation = new();
        summarizeConversation.Add(ConversationMessage.FromText(ConversationRole.System, SummarizationSystemPrompt));

        foreach (ConversationMessage message in branchConversation.Messages)
        {
            summarizeConversation.Add(message);
        }

        string userPrompt = string.IsNullOrWhiteSpace(instructions)
            ? DefaultSummarizationUserPrompt
            : instructions;
        summarizeConversation.Add(ConversationMessage.FromText(ConversationRole.User, userPrompt));

        string summary = string.Empty;
        await foreach (AgentEvent agentEvent in _runtime.RunAsync(
                           new AgentRunRequest(providerId, modelId, summarizeConversation, workspaceRoot),
                           cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent.Kind == AgentEventKind.Failed)
            {
                throw new InvalidOperationException(agentEvent.Message);
            }

            if (agentEvent.Kind != AgentEventKind.Completed || agentEvent.TurnArtifacts is null)
            {
                continue;
            }

            ConversationMessage? assistant = agentEvent.TurnArtifacts.Messages
                .LastOrDefault(static message => message.Role == ConversationRole.Assistant);
            summary = assistant?.Text ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new InvalidOperationException("Compaction summarization produced no output.");
        }

        return summary;
    }

    private static int CountConversationChars(ConversationState conversation)
    {
        int total = 0;
        foreach (ConversationMessage message in conversation.Messages)
        {
            total += message.Text.Length;
        }

        return total;
    }

    private static long? FindLastUsageTokens(IReadOnlyList<SessionEntry> branch)
    {
        for (int index = branch.Count - 1; index >= 0; index--)
        {
            if (branch[index] is not MessageSessionEntry { Message.Role: ConversationRole.Assistant } messageEntry)
            {
                continue;
            }

            if (messageEntry.Message.Usage?.TotalTokens is long tokens)
            {
                return tokens;
            }
        }

        return null;
    }
}

internal sealed record CompactionResult(bool Succeeded, string? Message, int CharsBefore = 0, int CharsAfter = 0)
{
    public static CompactionResult Failed(string message) => new(false, message);

    public static CompactionResult Completed(int charsBefore, int charsAfter) =>
        new(
            true,
            $"Compaction complete. Active context reduced from ~{charsBefore:N0} to ~{charsAfter:N0} characters.",
            charsBefore,
            charsAfter);
}