namespace WinHarness.Diagnostics;

/// <summary>
/// Writes structured diagnostic records.
/// </summary>
public interface IDiagnosticSink
{
    /// <summary>
    /// Writes a diagnostic record.
    /// </summary>
    ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Structured diagnostic record.
/// </summary>
public sealed record DiagnosticRecord(
    DateTimeOffset Timestamp,
    string Category,
    string EventName,
    string Message,
    IReadOnlyDictionary<string, string> Properties);
