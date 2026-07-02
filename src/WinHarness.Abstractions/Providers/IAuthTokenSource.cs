using System.Text.Json.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Resolves a bearer credential for provider requests. Implementations must be
/// safe to call per request: OAuth access tokens can be short-lived (GitHub
/// Copilot bearers last ~30 minutes), so callers never cache the result.
/// </summary>
public interface IAuthTokenSource
{
    /// <summary>
    /// Returns a currently valid bearer token, refreshing transparently when
    /// the stored token is near expiry.
    /// </summary>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Refreshes an expired OAuth token set. Implementations are provider-flow
/// specific (Copilot token exchange, Anthropic/OpenAI refresh grant).
/// </summary>
public interface IOAuthTokenRefresher
{
    /// <summary>
    /// The OAuth provider id this refresher supports (e.g. "copilot").
    /// </summary>
    string OAuthProviderId { get; }

    /// <summary>
    /// Exchanges the stored token set for a fresh one.
    /// </summary>
    ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken);
}

/// <summary>
/// A stored OAuth token set. Persisted as one JSON secret per provider under
/// <c>WinHarness:oauth:&lt;provider-id&gt;</c> in the credential store.
/// </summary>
public sealed record OAuthTokenSet(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("scopes")] string? Scopes = null,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null)
{
    /// <summary>
    /// Refresh this many minutes before the reported expiry.
    /// </summary>
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the access token is expired or within the refresh skew.
    /// </summary>
    public bool NeedsRefresh(TimeProvider time) =>
        ExpiresAt is { } expiry && time.GetUtcNow() >= expiry - RefreshSkew;
}

/// <summary>
/// Credential-store naming for OAuth token sets.
/// </summary>
public static class OAuthCredentialNames
{
    /// <summary>
    /// Builds the credential target name for a provider's OAuth token set.
    /// </summary>
    public static string ForProvider(string providerId) => $"WinHarness:oauth:{providerId}";
}
