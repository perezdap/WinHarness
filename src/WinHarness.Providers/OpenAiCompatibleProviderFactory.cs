using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using WinHarness.Configuration;
using WinHarness.Platform;

namespace WinHarness.Providers;

/// <summary>
/// Creates OpenAI-compatible chat providers from configuration.
/// </summary>
public sealed class OpenAiCompatibleProviderFactory : IProviderFactory
{
    private readonly WinHarnessOptions _options;
    private readonly ICredentialStore _credentialStore;
    private readonly IEnumerable<IOAuthTokenRefresher> _refreshers;

    /// <summary>
    /// Creates a factory.
    /// </summary>
    public OpenAiCompatibleProviderFactory(WinHarnessOptions options, ICredentialStore credentialStore)
        : this(options, credentialStore, [])
    {
    }

    /// <summary>
    /// Creates a factory with OAuth flow refreshers.
    /// </summary>
    public OpenAiCompatibleProviderFactory(
        WinHarnessOptions options,
        ICredentialStore credentialStore,
        IEnumerable<IOAuthTokenRefresher> refreshers)
    {
        _options = options;
        _credentialStore = credentialStore;
        _refreshers = refreshers;
    }

    /// <inheritdoc />
    public IChatProvider Create(string providerId, string modelId)
    {
        ProviderOptions provider = _options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        ModelOptions model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{providerId}'.");

        return new OpenAiCompatibleChatProvider(provider, model, CreateTokenSource(provider));
    }

    /// <summary>
    /// Resolves the auth token source for a configured provider: OAuth when the
    /// auth block requests it (and a matching flow refresher is registered),
    /// api-key otherwise.
    /// </summary>
    public IAuthTokenSource CreateTokenSource(ProviderOptions provider)
    {
        if (provider.Auth is { Scheme: var scheme } auth &&
            string.Equals(scheme, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            IOAuthTokenRefresher refresher = _refreshers.FirstOrDefault(candidate =>
                string.Equals(candidate.OAuthProviderId, auth.OAuthProvider, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Provider '{provider.Id}' uses OAuth flow '{auth.OAuthProvider}', but no such flow is available.");

            return new OAuthTokenSource(_credentialStore, refresher, provider.Id);
        }

        return new ApiKeyTokenSource(_credentialStore, provider.CredentialName);
    }
}

internal sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly ProviderOptions _provider;
    private readonly ModelOptions _model;
    private readonly IAuthTokenSource _tokenSource;

    public OpenAiCompatibleChatProvider(
        ProviderOptions provider,
        ModelOptions model,
        IAuthTokenSource tokenSource)
    {
        _provider = provider;
        _model = model;
        _tokenSource = tokenSource;
    }

    public string ProviderId => _provider.Id;

    public string ModelId => _model.Id;

    public ProviderCapabilities Capabilities => _model.Capabilities;

    public IChatClient CreateChatClient()
    {
        string apiKey = ResolveApiKey();

        ChatClient client = new(
            model: _model.ProviderModelId,
            credential: new ApiKeyCredential(apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = _provider.BaseUrl is null ? null : new Uri(_provider.BaseUrl)
            });

        return client.AsIChatClient();
    }

    private string ResolveApiKey()
    {
        // Providers resolve the credential per client creation; OAuth token
        // sources refresh transparently inside GetAccessTokenAsync.
        return _tokenSource.GetAccessTokenAsync(CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
}
