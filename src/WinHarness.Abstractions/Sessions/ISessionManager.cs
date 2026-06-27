using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Sessions;

/// <summary>
/// Manages in-memory session tree state and persistence via <see cref="ISessionStore"/>.
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// Gets whether this session is backed by a JSONL file on disk.
    /// </summary>
    bool IsPersisted { get; }

    /// <summary>
    /// Gets the absolute path to the session file, or <see langword="null"/> when in-memory.
    /// </summary>
    string? SessionFilePath { get; }

    /// <summary>
    /// Gets the current leaf entry id, or <see langword="null"/> when the tree is empty.
    /// </summary>
    string? LeafEntryId { get; }

    /// <summary>
    /// Gets the session header.
    /// </summary>
    SessionHeader Header { get; }

    /// <summary>
    /// Gets the latest display name from <c>session_info</c> entries on the active branch.
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// Moves the active leaf to an earlier entry to enable branching.
    /// </summary>
    void BranchTo(string entryId);

    /// <summary>
    /// Gets entries on the active branch from root to leaf.
    /// </summary>
    IReadOnlyList<SessionEntry> GetActiveBranch();

    /// <summary>
    /// Gets direct child entries of the given entry.
    /// </summary>
    IReadOnlyList<SessionEntry> GetChildren(string entryId);

    /// <summary>
    /// Gets the full entry tree with active-branch highlighting.
    /// </summary>
    SessionTree GetTree();

    /// <summary>
    /// Appends conversation messages as chained <c>message</c> entries.
    /// </summary>
    /// <returns>The id of the last appended entry.</returns>
    ValueTask<string> AppendMessagesAsync(
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a <c>model_change</c> entry.
    /// </summary>
    /// <returns>The new entry id.</returns>
    ValueTask<string> AppendModelChangeAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a <c>compaction</c> entry.
    /// </summary>
    /// <returns>The new entry id.</returns>
    ValueTask<string> AppendCompactionAsync(
        string summary,
        string firstKeptEntryId,
        long? tokensBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a <c>session_info</c> entry.
    /// </summary>
    /// <returns>The new entry id.</returns>
    ValueTask<string> AppendSessionInfoAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Projects the active branch into a <see cref="ConversationState"/> with compaction overlay applied.
    /// Context files are injected separately at runtime (PR-5).
    /// </summary>
    ConversationState BuildConversation(string? skillSystemPrompt);
}