namespace WinHarness.Providers;

/// <summary>
/// Resolves configured chat providers.
/// </summary>
public interface IProviderFactory
{
    /// <summary>
    /// Creates a chat provider for the requested provider and model.
    /// </summary>
    IChatProvider Create(string providerId, string modelId);

    /// <summary>
    /// Resolves the auth token source for a configured provider: OAuth when the
    /// provider's <c>auth</c> block requests it (and a matching flow refresher
    /// is registered), api-key otherwise. Used by callers that need a bearer
    /// outside chat (e.g. <c>models discover --provider-id</c>).
    /// </summary>
    IAuthTokenSource CreateTokenSource(string providerId);
}
