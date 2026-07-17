using WinHarness.Conversation;
using WinHarness.Sessions;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Chat;

/// <summary>
/// The active branch of a session, loaded once from root to leaf, answering the
/// branch queries the CLI needs: last-entry-of-kind, message counts, usage
/// totals, and flattening to conversation messages.
/// </summary>
internal sealed class ActiveBranch
{
    private readonly IReadOnlyList<SessionEntry> _entries;

    private ActiveBranch(IReadOnlyList<SessionEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Loads the active branch (root to leaf) from a session manager.
    /// </summary>
    public static ActiveBranch Load(ISessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        return new ActiveBranch(sessionManager.GetActiveBranch());
    }

    /// <summary>
    /// Wraps an already-ordered entry list, e.g. entries from a validated import file.
    /// </summary>
    public static ActiveBranch FromEntries(IReadOnlyList<SessionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new ActiveBranch(entries);
    }

    /// <summary>
    /// Gets the branch entries from root to leaf.
    /// </summary>
    public IReadOnlyList<SessionEntry> Entries => _entries;

    /// <summary>
    /// Finds the most recent branch entry of type <typeparamref name="T"/>, or
    /// <see langword="null"/> when no entry matches.
    /// </summary>
    public T? LastOfType<T>(Func<T, bool>? predicate = null)
        where T : SessionEntry
    {
        for (int index = _entries.Count - 1; index >= 0; index--)
        {
            if (_entries[index] is T entry && (predicate is null || predicate(entry)))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Counts message entries on the branch.
    /// </summary>
    public int CountMessageEntries() => _entries.Count(static entry => entry is MessageSessionEntry);

    /// <summary>
    /// Sums assistant-message usage over the branch; unset token counts add zero.
    /// </summary>
    public (long InputTokens, long OutputTokens) SumAssistantUsage()
    {
        long input = 0;
        long output = 0;
        foreach (SessionEntry entry in _entries)
        {
            if (entry is MessageSessionEntry { Message: { Role: ConversationRole.Assistant, Usage: { } usage } })
            {
                input += usage.InputTokens ?? 0;
                output += usage.OutputTokens ?? 0;
            }
        }

        return (input, output);
    }

    /// <summary>
    /// Flattens the branch into its conversation messages, in branch order.
    /// </summary>
    public List<ConversationMessage> FlattenMessages() =>
        _entries
            .OfType<MessageSessionEntry>()
            .Select(static entry => entry.Message)
            .ToList();

    /// <summary>
    /// Sums message text length over a built conversation; the input for the
    /// chars-per-token estimation heuristic.
    /// </summary>
    public static long SumMessageTextChars(ConversationState conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        long total = 0;
        foreach (ConversationMessage message in conversation.Messages)
        {
            total += message.Text.Length;
        }

        return total;
    }
}
