using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Orchestrates the deletion of session files, preventing the deletion of the currently active session.
/// </summary>
internal sealed class SessionDeletionService
{
    private readonly SessionManagerFactory _factory;

    public SessionDeletionService(SessionManagerFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Deletes the specified session file.
    /// </summary>
    /// <param name="path">The path of the session file to delete.</param>
    /// <param name="permanent">If true, deletes permanently. If false, moves to the .trash folder.</param>
    /// <param name="activeSessionPath">The path to the currently open active session, which cannot be deleted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when attempting to delete the active session.</exception>
    public async ValueTask<SessionDeletionResult> DeleteAsync(
        string path,
        bool permanent,
        string? activeSessionPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        if (activeSessionPath is not null && string.Equals(fullPath, Path.GetFullPath(activeSessionPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot delete the currently active session.");
        }

        return await _factory.DeleteAsync(fullPath, permanent, cancellationToken).ConfigureAwait(false);
    }
}
