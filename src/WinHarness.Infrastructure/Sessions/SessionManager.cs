using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;
using WinHarness.Sessions;

namespace WinHarness.Infrastructure.Sessions;

/// <summary>
/// In-memory session tree with optional JSONL persistence.
/// </summary>
public sealed class SessionManager : ISessionManager
{
    private readonly ISessionStore? _store;
    private readonly List<SessionEntry> _entries;
    private readonly Dictionary<string, SessionEntry> _entriesById;
    private string? _leafEntryId;

    private SessionManager(
        ISessionStore? store,
        SessionHeader header,
        string? sessionFilePath,
        IReadOnlyList<SessionEntry> entries,
        string? leafEntryId)
    {
        _store = store;
        Header = header;
        SessionFilePath = sessionFilePath;
        IsPersisted = sessionFilePath is not null;
        _entries = [.. entries];
        _entriesById = _entries.ToDictionary(static entry => entry.Id);
        _leafEntryId = leafEntryId;
    }

    /// <inheritdoc />
    public bool IsPersisted { get; }

    /// <inheritdoc />
    public string? SessionFilePath { get; }

    /// <inheritdoc />
    public string? LeafEntryId => _leafEntryId;

    /// <inheritdoc />
    public SessionHeader Header { get; }

    /// <inheritdoc />
    public string? DisplayName
    {
        get
        {
            foreach (SessionEntry entry in GetActiveBranch().AsEnumerable().Reverse())
            {
                if (entry is SessionInfoSessionEntry infoEntry)
                {
                    return infoEntry.Name;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Creates an in-memory session that is not persisted to disk.
    /// </summary>
    public static ISessionManager InMemory(string cwd)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        string absoluteCwd = Path.GetFullPath(cwd);
        SessionHeader header = new(
            Id: Guid.NewGuid().ToString("D"),
            Timestamp: DateTimeOffset.UtcNow,
            Cwd: absoluteCwd,
            ParentSession: null);

        return new SessionManager(null, header, null, [], null);
    }

    /// <summary>
    /// Creates a session manager from a loaded session file.
    /// </summary>
    public static ISessionManager FromFile(ISessionStore store, SessionFile file)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(file);

        string? leafEntryId = file.Entries.Count > 0 ? file.Entries[^1].Id : null;
        return new SessionManager(store, file.Header, file.Path, file.Entries, leafEntryId);
    }

    /// <inheritdoc />
    public void BranchTo(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!_entriesById.ContainsKey(entryId))
        {
            throw new ArgumentException($"Session entry '{entryId}' was not found.", nameof(entryId));
        }

        _leafEntryId = entryId;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionEntry> GetActiveBranch()
    {
        if (_leafEntryId is null)
        {
            return [];
        }

        List<SessionEntry> branch = [];
        string? currentId = _leafEntryId;
        while (currentId is not null && _entriesById.TryGetValue(currentId, out SessionEntry? entry))
        {
            branch.Add(entry);
            currentId = entry.ParentId;
        }

        branch.Reverse();
        return branch;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionEntry> GetChildren(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        if (!_entriesById.ContainsKey(entryId))
        {
            throw new ArgumentException($"Session entry '{entryId}' was not found.", nameof(entryId));
        }

        List<SessionEntry> children = [];
        foreach (SessionEntry entry in _entries)
        {
            if (string.Equals(entry.ParentId, entryId, StringComparison.Ordinal))
            {
                children.Add(entry);
            }
        }

        return children;
    }

    /// <inheritdoc />
    public SessionTree GetTree()
    {
        HashSet<string> activeBranchIds = GetActiveBranch()
            .Select(static entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        Dictionary<string, List<string>> childIdsByParent = [];
        foreach (SessionEntry entry in _entries)
        {
            if (entry.ParentId is null)
            {
                continue;
            }

            if (!childIdsByParent.TryGetValue(entry.ParentId, out List<string>? childIds))
            {
                childIds = [];
                childIdsByParent[entry.ParentId] = childIds;
            }

            childIds.Add(entry.Id);
        }

        SessionTreeNode[] nodes = _entries
            .Select(entry => new SessionTreeNode(
                entry,
                childIdsByParent.TryGetValue(entry.Id, out List<string>? childIds)
                    ? childIds
                    : [],
                activeBranchIds.Contains(entry.Id)))
            .ToArray();

        return new SessionTree(_leafEntryId, nodes);
    }

    /// <inheritdoc />
    public async ValueTask<string> AppendMessagesAsync(
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return _leafEntryId ?? throw new InvalidOperationException("Cannot append an empty message list to an empty session.");
        }

        string? parentId = _leafEntryId;
        string lastEntryId = string.Empty;

        foreach (ConversationMessage message in messages)
        {
            string entryId = SessionEntryIds.Create();
            MessageSessionEntry entry = new(
                entryId,
                parentId,
                DateTimeOffset.UtcNow,
                message);

            lastEntryId = await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            parentId = lastEntryId;
        }

        return lastEntryId;
    }

    /// <inheritdoc />
    public ValueTask<string> AppendModelChangeAsync(
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        ModelChangeSessionEntry entry = new(
            SessionEntryIds.Create(),
            _leafEntryId,
            DateTimeOffset.UtcNow,
            providerId,
            modelId);

        return AppendEntryAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<string> AppendCompactionAsync(
        string summary,
        string firstKeptEntryId,
        long? tokensBefore,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstKeptEntryId);

        CompactionSessionEntry entry = new(
            SessionEntryIds.Create(),
            _leafEntryId,
            DateTimeOffset.UtcNow,
            summary,
            firstKeptEntryId,
            tokensBefore);

        return AppendEntryAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<string> AppendSessionInfoAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        SessionInfoSessionEntry entry = new(
            SessionEntryIds.Create(),
            _leafEntryId,
            DateTimeOffset.UtcNow,
            name);

        return AppendEntryAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public ConversationState BuildConversation(string? skillSystemPrompt) =>
        SessionContextBuilder.Build(GetActiveBranch(), skillSystemPrompt);

    private async ValueTask<string> AppendEntryAsync(SessionEntry entry, CancellationToken cancellationToken)
    {
        _entries.Add(entry);
        _entriesById[entry.Id] = entry;
        _leafEntryId = entry.Id;

        if (IsPersisted && _store is not null && SessionFilePath is not null)
        {
            await _store.AppendAsync(SessionFilePath, entry, cancellationToken).ConfigureAwait(false);
        }

        return entry.Id;
    }
}