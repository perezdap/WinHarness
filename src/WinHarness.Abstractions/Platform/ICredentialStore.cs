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
}
