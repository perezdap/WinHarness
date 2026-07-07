using System.Text.Json;

namespace WinHarness.Cli.Configuration;

/// <summary>
/// JSON parsing helpers for CLI string-array and string-dictionary arguments.
/// </summary>
internal static class JsonArgumentParser
{
    public static IReadOnlyList<string> ParseStringArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON string array.");
        }

        List<string> values = [];
        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Expected a JSON string array.");
            }

            values.Add(element.GetString() ?? string.Empty);
        }

        return values;
    }

    public static IReadOnlyDictionary<string, string> ParseStringDictionary(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected a JSON string object.");
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Expected a JSON string object.");
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return values;
    }

    public static IReadOnlyDictionary<string, string?> ParseNullableStringDictionary(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected a JSON string object.");
        }

        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null => null,
                _ => throw new InvalidOperationException("Expected a JSON string object.")
            };
        }

        return values;
    }

    public static Dictionary<string, string> MergeToolMetadata(
        Dictionary<string, string> properties,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return properties;
        }

        foreach (KeyValuePair<string, string> pair in metadata)
        {
            properties[pair.Key] = pair.Value;
        }

        return properties;
    }
}
