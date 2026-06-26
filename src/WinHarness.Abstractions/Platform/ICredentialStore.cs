namespace WinHarness.Platform;

/// <summary>
/// Stores and retrieves provider credentials.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Gets a credential secret by target name.
    /// </summary>
    ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a credential secret by target name.
    /// </summary>
    ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a credential secret by target name.
    /// </summary>
    ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken);
}
