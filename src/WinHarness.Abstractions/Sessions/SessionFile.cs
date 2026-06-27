namespace WinHarness.Sessions;

/// <summary>
/// In-memory representation of a loaded session file.
/// </summary>
public sealed class SessionFile
{
    /// <summary>
    /// Creates a session file snapshot.
    /// </summary>
    public SessionFile(string path, SessionHeader header, IReadOnlyList<SessionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);
        Path = path;
        Header = header;
        Entries = entries;
    }

    /// <summary>
    /// Gets the absolute path to the JSONL file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the session header (line 1).
    /// </summary>
    public SessionHeader Header { get; }

    /// <summary>
    /// Gets parsed tree entries in file order.
    /// </summary>
    public IReadOnlyList<SessionEntry> Entries { get; }
}