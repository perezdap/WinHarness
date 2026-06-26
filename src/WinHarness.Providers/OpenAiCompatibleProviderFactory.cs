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

    /// <summary>
    /// Creates a factory.
    /// </summary>
    public OpenAiCompatibleProviderFactory(WinHarnessOptions options, ICredentialStore credentialStore)
    {
        _options = options;
        _credentialStore = credentialStore;
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

        return new OpenAiCompatibleChatProvider(provider, model, _credentialStore);
    }
}

internal sealed class OpenAiCompatibleChatProvider : IChatProvider
{
    private readonly ProviderOptions _provider;
    private readonly ModelOptions _model;
    private readonly ICredentialStore _credentialStore;

    public OpenAiCompatibleChatProvider(
        ProviderOptions provider,
        ModelOptions model,
        ICredentialStore credentialStore)
    {
        _provider = provider;
        _model = model;
        _credentialStore = credentialStore;
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

        return client.AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }

    private string ResolveApiKey()
    {
        if (string.IsNullOrWhiteSpace(_provider.CredentialName))
        {
            return "not-required";
        }

        return _credentialStore.GetSecretAsync(_provider.CredentialName, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            ?? throw new InvalidOperationException($"Credential '{_provider.CredentialName}' was not found.");
    }
}
