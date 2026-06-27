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
}