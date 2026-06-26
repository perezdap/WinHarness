using Microsoft.Extensions.DependencyInjection;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Infrastructure.Diagnostics;

namespace WinHarness.Infrastructure;

/// <summary>
/// Infrastructure service registration.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Adds infrastructure services.
    /// </summary>
    public static IServiceCollection AddWinHarnessInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IDiagnosticSink, JsonlDiagnosticSink>();
        services.AddSingleton(static _ => new ConfigStore());
        services.AddSingleton<ProviderConfigurator>();
        return services;
    }
}
