using System.Reflection;
using System.Text;

namespace WinHarness.Tools.Docs;

/// <summary>
/// Embedded WinHarness agent documentation topics shipped inside the tools assembly.
/// </summary>
public static class WinHarnessDocsCatalog
{
    private const string ResourcePrefix = "WinHarness.Docs.topics.";
    private const int DefaultMaxOutputBytes = 16 * 1024;

    private static readonly DocTopic[] Topics =
    [
        new("paths", "Configuration and data directory layout"),
        new("sessions", "Persisted JSONL chat sessions"),
        new("tools", "Built-in workspace tools and run_command rules"),
        new("providers", "Providers, models, login, and reasoning effort"),
        new("mcp", "MCP server configuration"),
        new("chat", "REPL slash commands and chat flags"),
        new("diagnostics", "Structured logs and timing fields"),
    ];

    public static IReadOnlyList<DocTopic> ListTopics() => Topics;

    public static string FormatCatalog()
    {
        StringBuilder builder = new();
        foreach (DocTopic topic in Topics)
        {
            builder.Append(topic.Id);
            builder.Append(" — ");
            builder.AppendLine(topic.Summary);
        }

        return builder.ToString().TrimEnd();
    }

    public static DocLookupResult Lookup(string? topicId, string? query, int maxOutputBytes)
    {
        if (string.IsNullOrWhiteSpace(topicId))
        {
            return new DocLookupResult(true, FormatCatalog(), null);
        }

        string normalizedId = topicId.Trim();
        DocTopic? topic = Topics.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, normalizedId, StringComparison.OrdinalIgnoreCase));
        if (topic is null)
        {
            string message = $"Unknown topic '{normalizedId}'. Valid topics: {string.Join(", ", Topics.Select(static t => t.Id))}.";
            return new DocLookupResult(false, message, "unknown_topic");
        }

        string? body = LoadTopicBody(topic.Id);
        if (body is null)
        {
            return new DocLookupResult(false, $"Topic '{topic.Id}' is registered but its embedded document is missing.", "missing_topic");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            body = FilterByQuery(body, query);
        }

        int limit = maxOutputBytes > 0 ? maxOutputBytes : DefaultMaxOutputBytes;
        body = Truncate(body, limit);
        return new DocLookupResult(true, body, null);
    }

    private static string? LoadTopicBody(string topicId)
    {
        string resourceName = ResourcePrefix + topicId + ".md";
        Assembly assembly = typeof(WinHarnessDocsCatalog).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string FilterByQuery(string body, string query)
    {
        string needle = query.Trim();
        if (needle.Length == 0)
        {
            return body;
        }

        StringBuilder builder = new();
        foreach (string line in body.Split('\n'))
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(line.TrimEnd('\r'));
            }
        }

        return builder.Length == 0
            ? $"No lines matched query '{needle}'."
            : builder.ToString();
    }

    private static string Truncate(string content, int maxBytes)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length <= maxBytes)
        {
            return content;
        }

        int limit = Math.Max(0, maxBytes);
        while (limit > 0 && (bytes[limit] & 0xC0) == 0x80)
        {
            limit--;
        }

        return Encoding.UTF8.GetString(bytes.AsSpan(0, limit)) + Environment.NewLine + "[output truncated]";
    }

    public sealed record DocTopic(string Id, string Summary);

    public sealed record DocLookupResult(bool Succeeded, string Content, string? ErrorCode);
}
