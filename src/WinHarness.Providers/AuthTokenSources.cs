using System.Text.Json;
using WinHarness.Platform;
using WinHarness.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Api-key scheme: reads a static secret from the credential store.
/// </summary>
public sealed class ApiKeyTokenSource : IAuthTokenSource
{
    private readonly ICredentialStore _credentialStore;
    private readonly string? _credentialName;

    /// <summary>
    /// Creates a token source for a provider's configured credentialName.
    /// Null credentialName means a keyless local endpoint.
    /// </summary>
    public ApiKeyTokenSource(ICredentialStore credentialStore, string? credentialName)
    {
        _credentialStore = credentialStore;
        _credentialName = credentialName;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_credentialName))
        {
            return "not-required";
        }

        return await _credentialStore.GetSecretAsync(_credentialName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential '{_credentialName}' was not found.");
    }
}

/// <summary>
/// OAuth scheme: reads the stored token set, refreshing through the flow's
/// <see cref="IOAuthTokenRefresher"/> when near expiry and persisting rotated
/// tokens back to the credential store (last-writer-wins across processes).
/// </summary>
public sealed class OAuthTokenSource : IAuthTokenSource
{
    private readonly ICredentialStore _credentialStore;
    private readonly IOAuthTokenRefresher _refresher;
    private readonly string _providerId;
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a token source for one configured provider.
    /// </summary>
    public OAuthTokenSource(
        ICredentialStore credentialStore,
        IOAuthTokenRefresher refresher,
        string providerId,
        TimeProvider? time = null)
    {
        _credentialStore = credentialStore;
        _refresher = refresher;
        _providerId = providerId;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        OAuthTokenSet tokens = await LoadTokenSetAsync(cancellationToken).ConfigureAwait(false);
        if (!tokens.NeedsRefresh(_time))
        {
            return tokens.AccessToken;
        }

        OAuthTokenSet refreshed = await _refresher.RefreshAsync(tokens, cancellationToken).ConfigureAwait(false);
        await SaveTokenSetAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed.AccessToken;
    }

    /// <summary>
    /// Loads the full stored token set (some flows need BaseUrl/AccountId
    /// alongside the bearer).
    /// </summary>
    public async ValueTask<OAuthTokenSet> LoadTokenSetAsync(CancellationToken cancellationToken)
    {
        string targetName = OAuthCredentialNames.ForProvider(_providerId);
        string secret = await _credentialStore.GetSecretAsync(targetName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No OAuth tokens stored for provider '{_providerId}'. Run 'winharness login --provider {_refresher.OAuthProviderId}'.");

        return JsonSerializer.Deserialize(secret, WinHarnessJsonSerializerContext.Default.OAuthTokenSet)
            ?? throw new InvalidOperationException($"Stored OAuth tokens for provider '{_providerId}' could not be parsed.");
    }

    private async ValueTask SaveTokenSetAsync(OAuthTokenSet tokens, CancellationToken cancellationToken)
    {
        string targetName = OAuthCredentialNames.ForProvider(_providerId);
        string secret = JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet);
        await _credentialStore.SetSecretAsync(targetName, secret, cancellationToken).ConfigureAwait(false);
    }
}
