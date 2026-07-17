using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Shared branch-choice building and formatting for the REPL <c>/tree</c> picker.
/// </summary>
internal static class SessionTreeChoices
{
    private const int PreviewMaxLength = 60;

    /// <summary>
    /// One selectable branch entry in the session tree picker.
    /// </summary>
    internal sealed record Choice(SessionEntry Entry, bool IsOnActiveBranch);

    /// <summary>
    /// Builds the active branch plus numbered children at the current leaf.
    /// </summary>
    public static IReadOnlyList<Choice> BuildChoices(ISessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);

        List<Choice> choices = sessionManager
            .GetActiveBranch()
            .Select(static entry => new Choice(entry, IsOnActiveBranch: true))
            .ToList();

        if (sessionManager.LeafEntryId is null)
        {
            return choices;
        }

        HashSet<string> existingIds = choices
            .Select(static choice => choice.Entry.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (SessionEntry child in sessionManager.GetChildren(sessionManager.LeafEntryId))
        {
            if (existingIds.Add(child.Id))
            {
                choices.Add(new Choice(child, IsOnActiveBranch: false));
            }
        }

        return choices;
    }

    /// <summary>
    /// Formats a choice for numbered REPL output.
    /// </summary>
    public static string FormatReplLine(Choice choice, int index)
    {
        string marker = choice.IsOnActiveBranch ? "*" : " ";
        return $"  {index + 1,2}{marker} {FormatEntry(choice.Entry)}";
    }

    /// <summary>
    /// Branches to <paramref name="selected"/> and returns transcript feedback lines.
    /// </summary>
    public static IReadOnlyList<string> ApplyBranch(
        ISessionManager sessionManager,
        SessionEntry selected,
        Action<string> onBranch)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(onBranch);

        if (string.Equals(selected.Id, sessionManager.LeafEntryId, StringComparison.Ordinal))
        {
            return [$"Already on entry {selected.Id}."];
        }

        onBranch(selected.Id);

        int messageCount = ActiveBranch.Load(sessionManager).CountMessageEntries();
        return
        [
            $"Branched to entry {selected.Id}.",
            $"Active branch rebuilt ({messageCount} message entries).",
        ];
    }

    internal static string FormatEntry(SessionEntry entry)
    {
        string preview = entry switch
        {
            MessageSessionEntry messageEntry => $"[{messageEntry.Message.Role}] {TruncatePreview(messageEntry.Message.Text)}",
            CompactionSessionEntry compactionEntry => $"[compaction] {TruncatePreview(compactionEntry.Summary)}",
            ModelChangeSessionEntry modelEntry => $"[model] {modelEntry.ProviderId}/{modelEntry.ModelId}",
            SessionInfoSessionEntry infoEntry => $"[name] {infoEntry.Name}",
            _ => $"[{entry.GetType().Name}]",
        };

        return $"{preview} ({entry.Id})";
    }

    private static string TruncatePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        string trimmed = text.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= PreviewMaxLength
            ? trimmed
            : trimmed[..PreviewMaxLength] + "...";
    }
}