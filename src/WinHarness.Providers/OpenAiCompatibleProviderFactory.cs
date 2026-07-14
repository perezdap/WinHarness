using System.ClientModel;
using System.ClientModel.Primitives;
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

        IAuthTokenSource tokenSource = CreateTokenSource(provider);
        TimeSpan requestTimeout = ResolveRequestTimeout(_options.RequestTimeoutSeconds);
        if (string.Equals(provider.Kind, "anthropic-messages", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicMessagesChatProvider(provider, model, tokenSource, requestTimeout);
        }

        if (string.Equals(provider.Kind, "openai-codex-responses", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiCodexResponsesChatProvider(provider, model, tokenSource, requestTimeout);
        }

        return new OpenAiCompatibleChatProvider(provider, model, tokenSource, requestTimeout);
    }

    /// <inheritdoc />
    public IAuthTokenSource CreateTokenSource(string providerId)
    {
        ProviderOptions provider = _options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        return CreateTokenSource(provider);
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
                MatchesOAuthProvider(candidate, auth.OAuthProvider))
                ?? throw new InvalidOperationException(
                    $"Provider '{provider.Id}' uses OAuth flow '{auth.OAuthProvider}', but no such flow is available.");

            return new OAuthTokenSource(_credentialStore, refresher, provider.Id);
        }

        return new ApiKeyTokenSource(_credentialStore, provider.CredentialName);
    }

    private static bool MatchesOAuthProvider(IOAuthTokenRefresher refresher, string? configuredProvider)
    {
        if (string.Equals(refresher.OAuthProviderId, configuredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Early Codex logins wrote "openai" into oauthProvider while the flow's
        // canonical id is "openai-codex". Keep those existing configurations
        // usable; new logins write the canonical id.
        return string.Equals(configuredProvider, "openai", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(refresher.OAuthProviderId, OpenAiCodexOAuthFlow.ProviderId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the per-request HTTP timeout from configuration: a non-positive
    /// value means an infinite timeout (high-effort reasoning models may spend
    /// a long time before the first token).
    /// </summary>
    private static TimeSpan ResolveRequestTimeout(int seconds)
        => seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
}

internal sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly ProviderOptions _provider;
    private readonly ModelOptions _model;
    private readonly IAuthTokenSource _tokenSource;
    private readonly TimeSpan _requestTimeout;

    public OpenAiCompatibleChatProvider(
        ProviderOptions provider,
        ModelOptions model,
        IAuthTokenSource tokenSource,
        TimeSpan requestTimeout)
    {
        _provider = provider;
        _model = model;
        _tokenSource = tokenSource;
        _requestTimeout = requestTimeout;
    }

    public string ProviderId => _provider.Id;

    public string ModelId => _model.Id;

    public ProviderCapabilities Capabilities => _model.Capabilities;

    public IChatClient CreateChatClient()
    {
        string apiKey = ResolveApiKey();

        HttpClient http = new() { Timeout = _requestTimeout };
        ChatClient client = new(
            model: _model.ProviderModelId,
            credential: new ApiKeyCredential(apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = _provider.BaseUrl is null ? null : new Uri(_provider.BaseUrl),
                Transport = new HttpClientPipelineTransport(http),
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

internal sealed class AnthropicMessagesChatProvider : IChatProvider
{
    private readonly ProviderOptions _provider;
    private readonly ModelOptions _model;
    private readonly IAuthTokenSource _tokenSource;
    private readonly TimeSpan _requestTimeout;

    public AnthropicMessagesChatProvider(
        ProviderOptions provider,
        ModelOptions model,
        IAuthTokenSource tokenSource,
        TimeSpan requestTimeout)
    {
        _provider = provider;
        _model = model;
        _tokenSource = tokenSource;
        _requestTimeout = requestTimeout;
    }

    public string ProviderId => _provider.Id;

    public string ModelId => _model.Id;

    public ProviderCapabilities Capabilities => _model.Capabilities;

    public IChatClient CreateChatClient()
    {
        string baseUrl = _provider.BaseUrl ?? AnthropicOAuthFlow.DefaultBaseUrl;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/v1/messages", UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException($"Provider '{_provider.Id}' baseUrl is not a valid absolute URI.");
        }

        bool useOAuth = _provider.Auth is { Scheme: var scheme } &&
            string.Equals(scheme, "oauth", StringComparison.OrdinalIgnoreCase);

        return new AnthropicMessagesChatClient(
            new HttpClient { Timeout = _requestTimeout },
            endpoint,
            _model.ProviderModelId,
            _tokenSource,
            useOAuth,
            ownsHttp: true);
    }
}

internal sealed class OpenAiCodexResponsesChatProvider : IChatProvider
{
    private readonly ProviderOptions _provider;
    private readonly ModelOptions _model;
    private readonly IAuthTokenSource _tokenSource;
    private readonly TimeSpan _requestTimeout;

    public OpenAiCodexResponsesChatProvider(
        ProviderOptions provider,
        ModelOptions model,
        IAuthTokenSource tokenSource,
        TimeSpan requestTimeout)
    {
        _provider = provider;
        _model = model;
        _tokenSource = tokenSource;
        _requestTimeout = requestTimeout;
    }

    public string ProviderId => _provider.Id;

    public string ModelId => _model.Id;

    public ProviderCapabilities Capabilities => _model.Capabilities;

    public IChatClient CreateChatClient()
    {
        string baseUrl = _provider.BaseUrl ?? OpenAiCodexOAuthFlow.DefaultBaseUrl;
        string endpointText = ResolveCodexResponsesUrl(baseUrl);
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException($"Provider '{_provider.Id}' baseUrl is not a valid absolute URI.");
        }

        string? accountId = null;
        if (_tokenSource is OAuthTokenSource oauthSource)
        {
            // Eager load so missing login surfaces at client creation with a clear message.
            OAuthTokenSet tokens = oauthSource.LoadTokenSetAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            accountId = tokens.AccountId;
        }

        return new OpenAiCodexResponsesChatClient(
            new HttpClient { Timeout = _requestTimeout },
            endpoint,
            _model.ProviderModelId,
            _tokenSource,
            accountId,
            ownsHttp: true);
    }

    private static string ResolveCodexResponsesUrl(string baseUrl)
    {
        string normalized = baseUrl.TrimEnd('/');
        if (normalized.EndsWith("/codex/responses", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/codex", StringComparison.OrdinalIgnoreCase))
        {
            return normalized + "/responses";
        }

        return normalized + "/codex/responses";
    }
}
