using Microsoft.Extensions.DependencyInjection;

namespace WinHarness.Providers;

/// <summary>
/// Provider dependency registration.
/// </summary>
public static class ProviderServiceCollectionExtensions
{
    /// <summary>
    /// Adds v0.1 provider services.
    /// </summary>
    public static IServiceCollection AddWinHarnessProviders(this IServiceCollection services)
    {
        services.AddSingleton<IModelCapabilityRegistry, ConfigurationModelCapabilityRegistry>();
        services.AddSingleton<IOAuthTokenRefresher>(static _ => new GitHubCopilotOAuthFlow(new HttpClient()));
        services.AddSingleton<IOAuthTokenRefresher>(static _ => new AnthropicOAuthFlow(new HttpClient()));
        services.AddSingleton<IOAuthTokenRefresher>(static _ => new OpenAiCodexOAuthFlow(new HttpClient()));
        services.AddSingleton<IProviderFactory>(static provider => new OpenAiCompatibleProviderFactory(
            provider.GetRequiredService<Configuration.WinHarnessOptions>(),
            provider.GetRequiredService<Platform.ICredentialStore>(),
            provider.GetServices<IOAuthTokenRefresher>()));
        services.AddSingleton<IModelCatalog, OpenAiCompatibleModelCatalog>();
        services.AddSingleton<IModelCapabilityInferrer, ModelCapabilityInferrer>();
        services.AddSingleton<IOpenRouterModelCatalog, OpenRouterModelCatalog>();
        services.AddSingleton<IModelCapabilityResolver, ModelCapabilityResolver>();
        return services;
    }
}
