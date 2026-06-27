using System.Text.Json.Serialization;

namespace WinHarness.Sessions;

/// <summary>
/// First line of a session JSONL file (not part of the entry tree).
/// </summary>
public sealed record SessionHeader(
    string Id,
    DateTimeOffset Timestamp,
    string Cwd,
    string? ParentSession,
    int Version = 1)
{
    /// <summary>
    /// JSON discriminator for session headers.
    /// </summary>
    public const string EntryType = "session";

    /// <summary>
    /// Gets the JSON type discriminator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type => EntryType;
}