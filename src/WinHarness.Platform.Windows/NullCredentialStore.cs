namespace WinHarness.Platform;

/// <summary>
/// Development credential store used until the Windows Credential Manager implementation is wired.
/// </summary>
public sealed class NullCredentialStore : ICredentialStore
{
    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<string?>(null);
    }
}
