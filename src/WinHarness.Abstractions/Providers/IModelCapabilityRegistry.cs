namespace WinHarness.Providers;

/// <summary>
/// Resolves model-specific provider capabilities.
/// </summary>
public interface IModelCapabilityRegistry
{
    /// <summary>
    /// Gets capabilities for a configured provider model.
    /// </summary>
    ProviderCapabilities GetCapabilities(string providerId, string modelId);
}
