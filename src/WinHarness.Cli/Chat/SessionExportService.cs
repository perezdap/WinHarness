using System.Text;
using System.Text.Json;
using WinHarness.Conversation;
using WinHarness.Serialization;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Exports the active branch of a session to a self-contained HTML file or a
/// linear JSONL file, and validates/imports JSONL session files.
/// </summary>
internal static class SessionExportService
{
    /// <summary>
    /// Exports the session's active branch. Format inferred from the file
    /// extension: .html renders a standalone page, anything else writes JSONL.
    /// Returns the absolute output path.
    /// </summary>
    public static string Export(ISessionManager session, string outputPath)
    {
        if (!session.IsPersisted)
        {
            throw new InvalidOperationException("Export requires a persisted session.");
        }

        string fullPath = Path.GetFullPath(outputPath);
        IReadOnlyList<SessionEntry> branch = session.GetActiveBranch();

        if (string.Equals(Path.GetExtension(fullPath), ".html", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(fullPath, RenderHtml(session, branch), Encoding.UTF8);
        }
        else
        {
            using StreamWriter writer = new(fullPath, append: false, new UTF8Encoding(false));
            foreach (SessionEntry entry in branch)
            {
                writer.WriteLine(JsonSerializer.Serialize(entry, WinHarnessJsonSerializerContext.Default.SessionEntry));
            }
        }

        return fullPath;
    }

    /// <summary>
    /// Validates a JSONL session file and returns its entries. Throws with a
    /// line-numbered message on malformed content.
    /// </summary>
    public static IReadOnlyList<SessionEntry> ValidateImportFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Import file '{path}' was not found.");
        }

        List<SessionEntry> entries = [];
        HashSet<string> seenIds = new(StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string line in File.ReadLines(fullPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SessionEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, WinHarnessJsonSerializerContext.Default.SessionEntry);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Import failed: line {lineNumber} is not a valid session entry ({ex.Message}).");
            }

            if (entry is null || string.IsNullOrEmpty(entry.Id))
            {
                throw new InvalidOperationException($"Import failed: line {lineNumber} is missing an entry id.");
            }

            if (entry.ParentId is not null && !seenIds.Contains(entry.ParentId))
            {
                throw new InvalidOperationException(
                    $"Import failed: line {lineNumber} references unknown parent '{entry.ParentId}'.");
            }

            seenIds.Add(entry.Id);
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Import failed: the file contains no session entries.");
        }

        return entries;
    }

    private static string RenderHtml(ISessionManager session, IReadOnlyList<SessionEntry> branch)
    {
        string title = Escape(session.DisplayName ?? session.Header.Id);
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<title>").Append(title).AppendLine("</title>");
        html.AppendLine("""
<style>
body{font-family:system-ui,Segoe UI,sans-serif;max-width:52rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#1a1a1a}
.msg{margin:1rem 0;padding:.75rem 1rem;border-radius:.5rem;white-space:pre-wrap;overflow-wrap:anywhere}
.user{background:#e8f0fe;border-left:4px solid #1a73e8}
.assistant{background:#f6f6f6;border-left:4px solid #7a7a7a}
.meta{color:#666;font-size:.85rem;margin-bottom:.25rem}
details{margin:.5rem 0;padding:.5rem .75rem;background:#fbf3e4;border-radius:.4rem}
summary{cursor:pointer;font-family:ui-monospace,monospace;font-size:.9rem}
pre{background:#f0f0f0;padding:.5rem;border-radius:.3rem;overflow-x:auto;font-size:.85rem}
.compaction{background:#eef9ee;border-left:4px solid #34a853;padding:.75rem 1rem;border-radius:.5rem;font-style:italic}
</style></head><body>
""");
        html.Append("<h1>").Append(title).AppendLine("</h1>");
        html.Append("<p class=\"meta\">Exported ").Append(Escape(DateTimeOffset.Now.ToString("u")))
            .Append(" · ").Append(branch.Count).AppendLine(" entries</p>");

        foreach (SessionEntry entry in branch)
        {
            switch (entry)
            {
                case MessageSessionEntry { Message: var message }:
                    RenderMessage(html, message);
                    break;
                case CompactionSessionEntry compaction:
                    html.Append("<div class=\"compaction\">Compaction: ")
                        .Append(Escape(Truncate(compaction.Summary, 2000)))
                        .AppendLine("</div>");
                    break;
                case ModelChangeSessionEntry modelChange:
                    html.Append("<p class=\"meta\">Model switched to ")
                        .Append(Escape($"{modelChange.ProviderId}/{modelChange.ModelId}"))
                        .AppendLine("</p>");
                    break;
                default:
                    break;
            }
        }

        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private static void RenderMessage(StringBuilder html, ConversationMessage message)
    {
        foreach (ContentBlock block in message.Content)
        {
            switch (block.Kind)
            {
                case ContentBlockKind.Text when message.Role == ConversationRole.User:
                    html.Append("<div class=\"msg user\"><div class=\"meta\">user</div>")
                        .Append(Escape(block.Text ?? "")).AppendLine("</div>");
                    break;

                case ContentBlockKind.Text when message.Role == ConversationRole.Assistant:
                    html.Append("<div class=\"msg assistant\"><div class=\"meta\">assistant</div>")
                        .Append(Escape(block.Text ?? "")).AppendLine("</div>");
                    break;

                case ContentBlockKind.ToolCall:
                    html.Append("<details><summary>tool call: ").Append(Escape(block.ToolName ?? "unknown"))
                        .Append("</summary><pre>").Append(Escape(block.ArgumentsJson ?? "{}"))
                        .AppendLine("</pre></details>");
                    break;

                case ContentBlockKind.ToolResult:
                    html.Append("<details><summary>tool result: ").Append(Escape(block.ToolName ?? "unknown"))
                        .Append(block.IsError ? " (error)" : "")
                        .Append("</summary><pre>").Append(Escape(Truncate(block.Text ?? "", 20000)))
                        .AppendLine("</pre></details>");
                    break;

                default:
                    break;
            }
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… (truncated)";

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
