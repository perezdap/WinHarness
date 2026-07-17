using WinHarness.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Owns all mutations to provider, model, and default configuration plus the
/// associated credential lifecycle. Callers (CLI commands, the config wizard,
/// the REPL) describe <em>what</em> they want changed; this type performs the
/// read-modify-write against <see cref="ConfigStore"/> and keeps the Windows
/// Credential Manager entry in sync. It never prompts and never writes to the
/// console, so it is reusable from any front end.
/// </summary>
public sealed class ProviderConfigurator
{
    private readonly ConfigStore _store;
    private readonly ICredentialStore _credentialStore;

    /// <summary>
    /// Creates a configurator.
    /// </summary>
    public ProviderConfigurator(ConfigStore store, ICredentialStore credentialStore)
    {
        _store = store;
        _credentialStore = credentialStore;
    }

    /// <summary>
    /// Adds or replaces an OpenAI-compatible provider. When <paramref name="apiKey"/>
    /// is provided, it is stored in Windows Credential Manager under a derived
    /// target name and the provider records that reference; otherwise the
    /// provider is treated as keyless (for example a local Ollama endpoint).
    /// Returns the persisted provider definition.
    /// </summary>
    public async ValueTask<ProviderOptions> AddProviderAsync(
        string id,
        string baseUrl,
        string? apiKey,
        bool makeDefault,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"Base URL '{baseUrl}' is not an absolute URI.");
        }

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

        string? credentialName = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            credentialName = BuildCredentialName(id);
            await _credentialStore.SetSecretAsync(credentialName, apiKey, cancellationToken).ConfigureAwait(false);
        }

        ProviderOptions? existing = options.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

        ProviderOptions provider;
        if (existing is null)
        {
            provider = new ProviderOptions
            {
                Id = id,
                Kind = "openai-compatible",
                BaseUrl = baseUrl,
                CredentialName = credentialName
            };
            options.Providers.Add(provider);
        }
        else
        {
            existing.Kind = "openai-compatible";
            existing.BaseUrl = baseUrl;
            // Preserve an existing credential reference when no new key was supplied.
            existing.CredentialName = credentialName ?? existing.CredentialName;
            provider = existing;
        }

        if (makeDefault || options.Providers.Count == 1)
        {
            options.DefaultProvider = provider.Id;
            // The default model must belong to the default provider; repoint it
            // to the new provider's first model (or clear it when none exist yet).
            bool defaultModelValid = provider.Models.Any(model =>
                string.Equals(model.Id, options.DefaultModel, StringComparison.OrdinalIgnoreCase));
            if (!defaultModelValid)
            {
                options.DefaultModel = provider.Models.Count > 0 ? provider.Models[0].Id : string.Empty;
            }
        }

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return provider;
    }

    /// <summary>
    /// Adds or replaces a model under an existing provider. Optionally promotes
    /// it to the default model when its provider is (or becomes) the default.
    /// </summary>
    public async ValueTask<ModelOptions> AddModelAsync(
        string providerId,
        string modelId,
        string providerModelId,
        ProviderCapabilities capabilities,
        bool makeDefault,
        int? contextWindow,
        List<string>? supportedReasoningEfforts,
        string? reasoningEffort = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerModelId);

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        ModelOptions? existing = provider.Models.FirstOrDefault(model =>
            string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));

        ModelOptions model;
        if (existing is null)
        {
            model = new ModelOptions
            {
                Id = modelId,
                ProviderModelId = providerModelId,
                Capabilities = capabilities,
                ContextWindow = contextWindow,
                SupportedReasoningEfforts = supportedReasoningEfforts,
                ReasoningEffort = reasoningEffort
            };
            provider.Models.Add(model);
        }
        else
        {
            existing.ProviderModelId = providerModelId;
            existing.Capabilities = capabilities;
            existing.ContextWindow = contextWindow;
            existing.SupportedReasoningEfforts = supportedReasoningEfforts;
            existing.ReasoningEffort = reasoningEffort;
            model = existing;
        }

        bool providerIsDefault = string.Equals(options.DefaultProvider, provider.Id, StringComparison.OrdinalIgnoreCase);
        if ((makeDefault && providerIsDefault) || (providerIsDefault && options.DefaultModel.Length == 0))
        {
            options.DefaultModel = model.Id;
        }

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return model;
    }

    /// <summary>
    /// Replaces the capabilities of an existing model under a provider.
    /// Returns the updated model definition.
    /// </summary>
    public async ValueTask<ModelOptions> SetModelCapabilitiesAsync(
        string providerId,
        string modelId,
        ProviderCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        ModelOptions model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{providerId}'.");

        model.Capabilities = capabilities;
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return model;
    }

    /// <summary>
    /// Removes a model from a provider. Clears the default model when it pointed
    /// at the removed model.
    /// </summary>
    public async ValueTask RemoveModelAsync(string providerId, string modelId, CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        ModelOptions model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{providerId}'.");

        provider.Models.Remove(model);

        if (string.Equals(options.DefaultProvider, provider.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(options.DefaultModel, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            options.DefaultModel = provider.Models.Count > 0 ? provider.Models[0].Id : string.Empty;
        }

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a provider, its models, and any owned credential. Clears the
    /// default selection when it pointed at the removed provider.
    /// </summary>
    public async ValueTask RemoveProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        options.Providers.Remove(provider);

        if (string.Equals(options.DefaultProvider, provider.Id, StringComparison.OrdinalIgnoreCase))
        {
            options.DefaultProvider = string.Empty;
            options.DefaultModel = string.Empty;
        }

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(provider.CredentialName) &&
            provider.CredentialName.StartsWith("WinHarness:", StringComparison.Ordinal))
        {
            await _credentialStore.DeleteSecretAsync(provider.CredentialName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets the default provider. The default model is repaired when it does not
    /// belong to the new provider (see <see cref="RepairDefaultModel"/>). Returns
    /// the resulting default model id so callers can report whether it changed.
    /// </summary>
    public async ValueTask<string> SetDefaultProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = FindProviderOrThrow(options, providerId);

        options.DefaultProvider = provider.Id;
        options.DefaultModel = RepairDefaultModel(options, provider.Id, options.DefaultModel);

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return options.DefaultModel;
    }

    /// <summary>
    /// Sets the default model. The model must belong to the current default
    /// provider.
    /// </summary>
    public async ValueTask SetDefaultModelAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, options.DefaultProvider, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Configure a default provider before selecting a model.");

        ModelOptions model = FindModelOrThrow(provider, modelId);
        options.DefaultModel = model.Id;

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the default provider and (optionally) the default model.
    /// </summary>
    public async ValueTask SetDefaultsAsync(
        string providerId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        ProviderOptions provider = FindProviderOrThrow(options, providerId);

        options.DefaultProvider = provider.Id;

        if (modelId is not null)
        {
            ModelOptions model = FindModelOrThrow(provider, modelId);
            options.DefaultModel = model.Id;
        }

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the default model after a provider switch: keeps
    /// <paramref name="currentModelId"/> when it belongs to
    /// <paramref name="providerId"/>, otherwise falls back to the provider's
    /// first model (or empty when it has none). An empty current model is left
    /// alone so an unset default stays unset.
    /// </summary>
    public static string RepairDefaultModel(WinHarnessOptions options, string providerId, string currentModelId)
    {
        ProviderOptions provider = FindProviderOrThrow(options, providerId);

        if (currentModelId.Length == 0)
        {
            return currentModelId;
        }

        bool modelExists = provider.Models.Any(model =>
            string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase));
        if (modelExists)
        {
            return currentModelId;
        }

        return provider.Models.Count > 0 ? provider.Models[0].Id : string.Empty;
    }

    /// <summary>
    /// Derives a credential target name for a provider id.
    /// </summary>
    public static string BuildCredentialName(string providerId)
    {
        return "WinHarness:" + providerId;
    }

    private static ProviderOptions FindProviderOrThrow(WinHarnessOptions options, string providerId)
    {
        return options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

    private static ModelOptions FindModelOrThrow(ProviderOptions provider, string modelId)
    {
        return provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{provider.Id}'.");
    }
}
