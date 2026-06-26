using System.Text.Json;
using System.Text.Json.Serialization;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.Infrastructure.Diagnostics;

/// <summary>
/// Writes local JSONL diagnostics under the WinHarness configuration directory.
/// </summary>
public sealed class JsonlDiagnosticSink : IDiagnosticSink
{
    private readonly string _logDirectory;

    /// <summary>
    /// Creates a diagnostics sink.
    /// </summary>
    public JsonlDiagnosticSink()
        : this(Path.Combine(WinHarnessConfiguration.GetConfigurationDirectory(), "logs"))
    {
    }

    /// <summary>
    /// Creates a diagnostics sink.
    /// </summary>
    public JsonlDiagnosticSink(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_logDirectory);
        string path = Path.Combine(_logDirectory, "winharness.jsonl");
        string json = JsonSerializer.Serialize(record, DiagnosticsJsonSerializerContext.Default.DiagnosticRecord);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DiagnosticRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DiagnosticsJsonSerializerContext : JsonSerializerContext;
