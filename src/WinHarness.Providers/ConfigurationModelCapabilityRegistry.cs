using WinHarness.Configuration;

namespace WinHarness.Providers;

/// <summary>
/// Resolves model capabilities from WinHarness configuration.
/// </summary>
public sealed class ConfigurationModelCapabilityRegistry : IModelCapabilityRegistry
{
    private readonly WinHarnessOptions _options;

    /// <summary>
    /// Creates a capability registry.
    /// </summary>
    public ConfigurationModelCapabilityRegistry(WinHarnessOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public ProviderCapabilities GetCapabilities(string providerId, string modelId)
    {
        ProviderOptions provider = _options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        ModelOptions model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{providerId}'.");

        return model.Capabilities;
    }
}
