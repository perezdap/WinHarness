namespace WinHarness.Sessions;

/// <summary>
/// Outcome of a session file deletion request.
/// </summary>
public sealed record SessionDeletionResult(
    string RequestedPath,
    SessionDeletionStatus Status,
    string? FinalPath = null)
{
    /// <summary>
    /// Creates a successful deletion result.
    /// </summary>
    public static SessionDeletionResult Succeeded(string requestedPath, SessionDeletionStatus status, string finalPath) =>
        new(requestedPath, status, finalPath);

    /// <summary>
    /// Creates a result for a file that was not found.
    /// </summary>
    public static SessionDeletionResult NotFound(string requestedPath) =>
        new(requestedPath, SessionDeletionStatus.NotFound, null);

    /// <summary>
    /// Creates a result for a request that was cancelled by the caller.
    /// </summary>
    public static SessionDeletionResult Cancelled(string requestedPath) =>
        new(requestedPath, SessionDeletionStatus.Cancelled, null);
}

/// <summary>
/// The kind of deletion that was performed.
/// </summary>
public enum SessionDeletionStatus
{
    /// <summary>
    /// The file was moved to the <c>.trash</c> folder and may be recovered.
    /// </summary>
    Trashed,

    /// <summary>
    /// The file was permanently removed from disk.
    /// </summary>
    PermanentlyDeleted,

    /// <summary>
    /// No matching file was found on disk.
    /// </summary>
    NotFound,

    /// <summary>
    /// The deletion was cancelled (for example, user declined confirmation).
    /// </summary>
    Cancelled
}
