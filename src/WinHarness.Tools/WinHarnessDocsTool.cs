using System.Text.Json;
using WinHarness.Tools.Docs;

namespace WinHarness.Tools;

/// <summary>
/// Built-in lookup for embedded WinHarness agent documentation.
/// </summary>
public sealed class WinHarnessDocsTool : ITool
{
    private static readonly JsonElement InputSchemaElement = ParseSchema(
        """
        {
          "type": "object",
          "properties": {
            "topic": {
              "type": "string",
              "description": "Topic id: paths, sessions, tools, providers, mcp, chat, diagnostics. Omit to list the catalog."
            },
            "query": {
              "type": "string",
              "description": "Optional case-insensitive line filter within a topic."
            },
            "maxOutputBytes": {
              "type": "integer",
              "description": "Maximum UTF-8 bytes to return (default 16384)."
            }
          }
        }
        """);

    public string Name => "winharness_docs";

    public string Description =>
        "Look up embedded WinHarness documentation (paths, sessions, tools, providers, MCP, chat commands, diagnostics). Omit topic to list subjects; use query to filter lines within a topic.";

    public JsonElement InputSchema => InputSchemaElement;

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? topic = null;
        if (invocation.Arguments.TryGetProperty("topic", out JsonElement topicElement) &&
            topicElement.ValueKind == JsonValueKind.String)
        {
            topic = topicElement.GetString();
        }

        string? query = null;
        if (invocation.Arguments.TryGetProperty("query", out JsonElement queryElement) &&
            queryElement.ValueKind == JsonValueKind.String)
        {
            query = queryElement.GetString();
        }

        int maxOutputBytes = 16 * 1024;
        if (invocation.Arguments.TryGetProperty("maxOutputBytes", out JsonElement maxElement) &&
            maxElement.ValueKind == JsonValueKind.Number &&
            maxElement.TryGetInt32(out int parsedMax))
        {
            maxOutputBytes = parsedMax;
        }

        WinHarnessDocsCatalog.DocLookupResult result = WinHarnessDocsCatalog.Lookup(topic, query, maxOutputBytes);
        return ValueTask.FromResult(
            result.Succeeded
                ? new ToolResult(true, result.Content)
                : new ToolResult(false, result.Content, result.ErrorCode));
    }

    private static JsonElement ParseSchema(string schema)
    {
        using JsonDocument document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }
}
