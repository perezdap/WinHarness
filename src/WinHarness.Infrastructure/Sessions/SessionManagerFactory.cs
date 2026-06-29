using WinHarness.Sessions;

namespace WinHarness.Infrastructure.Sessions;

/// <summary>
/// Factory for creating and opening <see cref="ISessionManager"/> instances.
/// </summary>
public sealed class SessionManagerFactory
{
    private readonly ISessionStore _store;

    /// <summary>
    /// Creates a session manager factory.
    /// </summary>
    public SessionManagerFactory(ISessionStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Creates an in-memory session that is not persisted to disk.
    /// </summary>
    public ISessionManager InMemory(string cwd) => SessionManager.InMemory(cwd);

    /// <summary>
    /// Creates a new persisted session file for the working directory.
    /// </summary>
    public async ValueTask<ISessionManager> CreateAsync(string cwd, CancellationToken cancellationToken)
    {
        SessionFile file = await _store.CreateAsync(cwd, cancellationToken).ConfigureAwait(false);
        return SessionManager.FromFile(_store, file);
    }

    /// <summary>
    /// Opens an existing session file.
    /// </summary>
    public async ValueTask<ISessionManager> OpenAsync(string path, CancellationToken cancellationToken)
    {
        SessionFile file = await _store.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return SessionManager.FromFile(_store, file);
    }

    /// <summary>
    /// Opens the most recently modified session for the working directory, or creates a new one.
    /// </summary>
    public async ValueTask<ISessionManager> ContinueRecentAsync(string cwd, CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionSummary> summaries = await _store.ListAsync(cwd, cancellationToken).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            return await CreateAsync(cwd, cancellationToken).ConfigureAwait(false);
        }

        return await OpenAsync(summaries[0].FilePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists session summaries for the working directory.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionSummary>> ListAsync(string cwd, CancellationToken cancellationToken) =>
        _store.ListAsync(cwd, cancellationToken);

    /// <summary>
    /// Lists session summaries across all working directories.
    /// </summary>
    public ValueTask<IReadOnlyList<SessionSummary>> ListAllAsync(CancellationToken cancellationToken) =>
        _store.ListAllAsync(cancellationToken);

    /// <summary>
    /// Deletes a session file.
    /// </summary>
    public ValueTask<SessionDeletionResult> DeleteAsync(string path, bool permanent, CancellationToken cancellationToken) =>
        _store.DeleteAsync(path, permanent, cancellationToken);
}