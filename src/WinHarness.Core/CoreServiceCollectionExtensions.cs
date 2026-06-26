using Microsoft.Extensions.DependencyInjection;
using WinHarness.Runtime;

namespace WinHarness;

/// <summary>
/// Core runtime service registration.
/// </summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds core WinHarness services.
    /// </summary>
    public static IServiceCollection AddWinHarnessCore(this IServiceCollection services)
    {
        services.AddSingleton<IAgentRuntime, SingleAgentRuntime>();
        return services;
    }
}
