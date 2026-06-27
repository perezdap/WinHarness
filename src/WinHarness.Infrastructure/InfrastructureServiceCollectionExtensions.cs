using Microsoft.Extensions.DependencyInjection;
using WinHarness.Context;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Infrastructure.Context;
using WinHarness.Infrastructure.Diagnostics;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

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
        services.AddSingleton<IContextFileLoader, ContextFileLoader>();
        services.AddSingleton<ISessionStore, JsonlSessionStore>();
        services.AddSingleton<SessionManagerFactory>();
        services.AddSingleton<ProviderConfigurator>();
        services.AddSingleton<McpConfigurator>();
        return services;
    }
}
