using System.Text.Json;

namespace WinHarness.Tools;

/// <summary>
/// Produces a short, safe-to-print summary of a tool invocation. The label
/// never includes arbitrary command or search text, because those fields can
/// carry credentials. Structured file paths are the only argument detail
/// permitted in terminal output.
/// </summary>
public static class ToolActivitySummarizer
{
    public const int MaxLength = 80;

    /// <summary>
    /// Builds a display label for the named tool given its arguments JSON.
    /// Falls back to the tool name unless the tool exposes a structured,
    /// display-safe file path.
    /// </summary>
    public static string? Build(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return null;
        }

        string? detail = toolName switch
        {
            "read_file" or "write_file" or "edit_file" => GetString(ParseArguments(argumentsJson), "path"),
            _ => null,
        };

        detail = Sanitize(detail);
        return detail is null
            ? Truncate(toolName)
            : Truncate(toolName + " " + detail);
    }

    private static JsonElement ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return default;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static string? Sanitize(string? detail)
    {
        if (detail is null)
        {
            return null;
        }

        string single = detail
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        if (single.Length == 0)
        {
            return null;
        }

        while (single.Contains("  ", StringComparison.Ordinal))
        {
            single = single.Replace("  ", " ", StringComparison.Ordinal);
        }

        return single;
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}