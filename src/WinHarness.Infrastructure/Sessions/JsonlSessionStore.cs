using System.Text;
using System.Text.Json;

using WinHarness.Conversation;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Serialization;
using WinHarness.Sessions;

namespace WinHarness.Infrastructure.Sessions;

/// <summary>
/// Append-only JSONL session store under the WinHarness configuration directory.
/// </summary>
public sealed class JsonlSessionStore : ISessionStore
{
    private const int PreviewMaxLength = 120;
    private readonly string _sessionsRoot;

    /// <summary>
    /// Creates a session store rooted at the default sessions directory.
    /// </summary>
    public JsonlSessionStore()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a session store with an optional sessions root override (for tests).
    /// </summary>
    public JsonlSessionStore(string? sessionsRoot)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(WinHarnessConfiguration.GetConfigurationDirectory(), "sessions");
    }

    /// <inheritdoc />
    public async ValueTask<SessionFile> CreateAsync(string cwd, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        string absoluteCwd = Path.GetFullPath(cwd);
        string workspaceKey = WorkspaceKeyNormalizer.FromCwd(absoluteCwd);
        string directory = Path.Combine(_sessionsRoot, workspaceKey);
        Directory.CreateDirectory(directory);

        string sessionId = SessionEntryIds.Create();
        string fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}_{sessionId}.jsonl";
        string path = Path.Combine(directory, fileName);

        SessionHeader header = new(
            Id: Guid.NewGuid().ToString("D"),
            Timestamp: DateTimeOffset.UtcNow,
            Cwd: absoluteCwd,
            ParentSession: null);

        await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(
                header,
                WinHarnessJsonSerializerContext.Default.SessionHeader);
            await WriteJsonLineAsync(stream, headerBytes, cancellationToken).ConfigureAwait(false);
        }

        return new SessionFile(path, header, []);
    }

    /// <inheritdoc />
    public async ValueTask<SessionFile> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Session file was not found.", path);
        }

        SessionHeader? header = null;
        List<SessionEntry> entries = [];

        await foreach (string line in ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (header is null)
            {
                header = JsonSerializer.Deserialize(line, WinHarnessJsonSerializerContext.Default.SessionHeader);
                if (header is null || !string.Equals(header.Type, SessionHeader.EntryType, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Session file is missing a valid header line.");
                }

                continue;
            }

            SessionEntry? entry = JsonSerializer.Deserialize(line, WinHarnessJsonSerializerContext.Default.SessionEntry);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("Session file is missing a header line.");
        }

        return new SessionFile(path, header, entries);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(string path, SessionEntry entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entry);

        await using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] entryBytes = JsonSerializer.SerializeToUtf8Bytes(
            entry,
            WinHarnessJsonSerializerContext.Default.SessionEntry);
        await WriteJsonLineAsync(stream, entryBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SessionSummary>> ListAsync(string cwd, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);
        string workspaceKey = WorkspaceKeyNormalizer.FromCwd(cwd);
        string directory = Path.Combine(_sessionsRoot, workspaceKey);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<SessionSummary> summaries = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionSummary? summary = await TryBuildSummaryAsync(path, cancellationToken).ConfigureAwait(false);
            if (summary is not null)
            {
                summaries.Add(summary);
            }
        }

        summaries.Sort(static (left, right) => right.LastModified.CompareTo(left.LastModified));
        return summaries;
    }

    private static async ValueTask<SessionSummary?> TryBuildSummaryAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            SessionFile session = await OpenForSummaryAsync(path, cancellationToken).ConfigureAwait(false);
            string sessionId = ExtractSessionId(session.Header.Id, path);
            string? displayName = null;
            string? firstUserPreview = null;
            int messageCount = 0;

            foreach (SessionEntry entry in session.Entries)
            {
                switch (entry)
                {
                    case MessageSessionEntry messageEntry:
                        messageCount++;
                        if (firstUserPreview is null &&
                            messageEntry.Message.Role == ConversationRole.User)
                        {
                            firstUserPreview = TruncatePreview(messageEntry.Message.Text);
                        }

                        break;
                    case SessionInfoSessionEntry infoEntry:
                        displayName = infoEntry.Name;
                        break;
                }
            }

            return new SessionSummary(
                path,
                sessionId,
                displayName,
                firstUserPreview,
                File.GetLastWriteTimeUtc(path),
                messageCount);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async ValueTask<SessionFile> OpenForSummaryAsync(string path, CancellationToken cancellationToken)
    {
        SessionHeader? header = null;
        List<SessionEntry> entries = [];

        await foreach (string line in ReadLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (header is null)
            {
                header = JsonSerializer.Deserialize(line, WinHarnessJsonSerializerContext.Default.SessionHeader);
                if (header is null || !string.Equals(header.Type, SessionHeader.EntryType, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Session file is missing a valid header line.");
                }

                continue;
            }

            try
            {
                SessionEntry? entry = JsonSerializer.Deserialize(line, WinHarnessJsonSerializerContext.Default.SessionEntry);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Skip malformed lines when building summaries.
            }
        }

        if (header is null)
        {
            throw new InvalidDataException("Session file is missing a header line.");
        }

        return new SessionFile(path, header, entries);
    }

    private static string ExtractSessionId(string headerId, string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        int separator = fileName.LastIndexOf('_');
        if (separator >= 0 && separator < fileName.Length - 1)
        {
            return fileName[(separator + 1)..];
        }

        return headerId.Replace("-", string.Empty, StringComparison.Ordinal)[..8];
    }

    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        return trimmed.Length <= PreviewMaxLength
            ? trimmed
            : trimmed[..PreviewMaxLength] + "...";
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SessionSummary>> ListAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return [];
        }

        List<SessionSummary> summaries = [];
        foreach (string subDir in Directory.EnumerateDirectories(_sessionsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string folderName = Path.GetFileName(subDir);
            if (string.Equals(folderName, ".trash", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(subDir, "*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SessionSummary? summary = await TryBuildSummaryAsync(path, cancellationToken).ConfigureAwait(false);
                if (summary is not null)
                {
                    summaries.Add(summary);
                }
            }
        }

        summaries.Sort(static (left, right) => right.LastModified.CompareTo(left.LastModified));
        return summaries;
    }

    /// <inheritdoc />
    public async ValueTask<SessionDeletionResult> DeleteAsync(string path, bool permanent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            return SessionDeletionResult.NotFound(path);
        }

        if (permanent)
        {
            File.Delete(fullPath);
            return SessionDeletionResult.Succeeded(path, SessionDeletionStatus.PermanentlyDeleted, fullPath);
        }

        string trashDirectory = Path.Combine(_sessionsRoot, ".trash");
        Directory.CreateDirectory(trashDirectory);

        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);
        string finalFileName = $"{fileNameWithoutExt}_{DateTimeOffset.UtcNow.Ticks}.jsonl";
        string destinationPath = Path.Combine(trashDirectory, finalFileName);

        File.Move(fullPath, destinationPath);
        return SessionDeletionResult.Succeeded(path, SessionDeletionStatus.Trashed, destinationPath);
    }

    private static async ValueTask WriteJsonLineAsync(
        FileStream stream,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}