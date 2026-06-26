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
}
