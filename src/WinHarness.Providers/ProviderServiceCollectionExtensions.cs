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
        services.AddSingleton<IProviderFactory, OpenAiCompatibleProviderFactory>();
        return services;
    }
}
