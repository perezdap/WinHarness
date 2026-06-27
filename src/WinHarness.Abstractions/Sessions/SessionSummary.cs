namespace WinHarness.Sessions;

/// <summary>
/// Summary metadata for session picker and resume flows.
/// </summary>
public sealed record SessionSummary(
    string FilePath,
    string SessionId,
    string? DisplayName,
    string? FirstUserPreview,
    DateTimeOffset LastModified,
    int MessageCount);