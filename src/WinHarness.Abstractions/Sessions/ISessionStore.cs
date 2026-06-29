namespace WinHarness.Sessions;

/// <summary>
/// Append-only persistence primitive for session JSONL files.
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// Creates a new session file for the given working directory.
    /// </summary>
    ValueTask<SessionFile> CreateAsync(string cwd, CancellationToken cancellationToken);

    /// <summary>
    /// Loads an existing session file from disk.
    /// </summary>
    ValueTask<SessionFile> OpenAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Appends one session entry to the file.
    /// </summary>
    ValueTask AppendAsync(string path, SessionEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Lists session summaries for the given working directory.
    /// </summary>
    ValueTask<IReadOnlyList<SessionSummary>> ListAsync(string cwd, CancellationToken cancellationToken);

    /// <summary>
    /// Lists session summaries across all working directories under the sessions root.
    /// </summary>
    ValueTask<IReadOnlyList<SessionSummary>> ListAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a session file. When <paramref name="permanent"/> is <see langword="false"/>
    /// the file is moved to a <c>.trash</c> folder under the sessions root so it can be
    /// recovered; when <see langword="true"/> it is removed permanently.
    /// </summary>
    ValueTask<SessionDeletionResult> DeleteAsync(string path, bool permanent, CancellationToken cancellationToken);
}
