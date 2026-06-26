using Microsoft.Extensions.AI;

namespace WinHarness.Providers;

/// <summary>
/// Creates provider-neutral chat clients for configured model endpoints.
/// </summary>
public interface IChatProvider
{
    /// <summary>
    /// Gets the configured provider identifier.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the configured model identifier.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Gets the model-specific provider capabilities.
    /// </summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>
    /// Creates the provider-neutral chat client.
    /// </summary>
    IChatClient CreateChatClient();
}
