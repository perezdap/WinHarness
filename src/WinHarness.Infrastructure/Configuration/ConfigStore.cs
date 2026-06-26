using System.Text.Json;
using WinHarness.Configuration;
using WinHarness.Serialization;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Reads and writes the full <see cref="WinHarnessOptions"/> document on disk
/// using the source-generated JSON contract (Native AOT safe). Use this for
/// edits that add or remove providers, models, and defaults; it always reads a
/// fresh copy from disk before mutating to avoid clobbering external edits.
/// </summary>
public sealed class ConfigStore
{
    private readonly string _path;

    /// <summary>
    /// Creates a config store rooted at the WinHarness configuration directory.
    /// </summary>
    public ConfigStore(string? configurationDirectory = null)
    {
        string directory = configurationDirectory ?? WinHarnessConfiguration.GetConfigurationDirectory();
        _path = System.IO.Path.Combine(directory, "config.json");
    }

    /// <summary>
    /// Gets the configuration file path.
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// Loads the configuration from disk, or returns an empty document when the
    /// file does not exist.
    /// </summary>
    public async ValueTask<WinHarnessOptions> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new WinHarnessOptions();
        }

        await using FileStream stream = File.OpenRead(_path);
        WinHarnessOptions? options = await JsonSerializer.DeserializeAsync(
            stream,
            WinHarnessJsonSerializerContext.Default.WinHarnessOptions,
            cancellationToken).ConfigureAwait(false);

        return options ?? new WinHarnessOptions();
    }

    /// <summary>
    /// Validates and writes the configuration to disk, creating the directory
    /// when needed.
    /// </summary>
    public async ValueTask SaveAsync(WinHarnessOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        WinHarnessOptionsValidator.Validate(options);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            options,
            WinHarnessJsonSerializerContext.Default.WinHarnessOptions);

        await File.WriteAllBytesAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
    }
}
