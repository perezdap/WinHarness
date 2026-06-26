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

    /// <inheritdoc />
    public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
