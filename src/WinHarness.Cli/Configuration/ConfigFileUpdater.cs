using System.Buffers;
using System.Text.Json;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.Cli.Configuration;

/// <summary>
/// Atomically updates root-level string properties in config.json.
/// </summary>
internal static class ConfigFileUpdater
{
    public static async ValueTask SetRootStringPropertyAsync(
        string propertyName,
        string value,
        CancellationToken cancellationToken)
    {
        await SetRootStringPropertiesAsync(
            new Dictionary<string, string> { [propertyName] = value },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically updates multiple root-level string properties in config.json,
    /// preserving all other properties.
    /// </summary>
    public static async ValueTask SetRootStringPropertiesAsync(
        IReadOnlyDictionary<string, string> updates,
        CancellationToken cancellationToken)
    {
        string directory = WinHarnessConfiguration.GetConfigurationDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");

        JsonDocument? document = File.Exists(path)
            ? JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            : null;

        ArrayBufferWriter<byte> buffer = new();
        try
        {
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                var written = new HashSet<string>(StringComparer.Ordinal);

                if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (updates.TryGetValue(property.Name, out string? replacement))
                        {
                            writer.WriteString(property.Name, replacement);
                            written.Add(property.Name);
                        }
                        else
                        {
                            writer.WritePropertyName(property.Name);
                            property.Value.WriteTo(writer);
                        }
                    }
                }

                foreach ((string key, string value) in updates)
                {
                    if (!written.Contains(key))
                    {
                        writer.WriteString(key, value);
                    }
                }

                writer.WriteEndObject();
            }
        }
        finally
        {
            document?.Dispose();
        }

        await AtomicFile.WriteAllBytesAsync(path, buffer.WrittenMemory.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
