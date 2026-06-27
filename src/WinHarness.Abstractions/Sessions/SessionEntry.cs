using System.Text.Json.Serialization;
using WinHarness.Conversation;

namespace WinHarness.Sessions;

/// <summary>
/// Base type for append-only session tree entries.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MessageSessionEntry), typeDiscriminator: "message")]
[JsonDerivedType(typeof(CompactionSessionEntry), typeDiscriminator: "compaction")]
[JsonDerivedType(typeof(ModelChangeSessionEntry), typeDiscriminator: "model_change")]
[JsonDerivedType(typeof(SessionInfoSessionEntry), typeDiscriminator: "session_info")]
public abstract record SessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp);

/// <summary>
/// A conversation message stored in the session tree.
/// </summary>
public sealed record MessageSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    ConversationMessage Message)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>
/// A compaction boundary that summarizes earlier context.
/// </summary>
public sealed record CompactionSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Summary,
    string FirstKeptEntryId,
    long? TokensBefore)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>
/// Records a provider/model switch for session restoration.
/// </summary>
public sealed record ModelChangeSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string ProviderId,
    string ModelId)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>
/// Sets the session display name (metadata only).
/// </summary>
public sealed record SessionInfoSessionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Name)
    : SessionEntry(Id, ParentId, Timestamp);

/// <summary>
/// Generates short session entry identifiers.
/// </summary>
public static class SessionEntryIds
{
    /// <summary>
    /// Creates an 8-character hex id (first segment of a UUID v4).
    /// </summary>
    public static string Create() => Guid.NewGuid().ToString("N")[..8];
}