using Microsoft.Extensions.DependencyInjection;
using WinHarness.Platform;

namespace WinHarness.Tools;

/// <summary>
/// Tool service registration.
/// </summary>
public static class ToolServiceCollectionExtensions
{
    /// <summary>
    /// Adds tool services.
    /// </summary>
    public static IServiceCollection AddWinHarnessTools(this IServiceCollection services)
    {
        services.AddSingleton(static provider => new BuiltinToolProvider(
            Environment.CurrentDirectory,
            provider.GetRequiredService<ICommandExecutor>()));
        services.AddSingleton<IToolProvider>(static provider => provider.GetRequiredService<BuiltinToolProvider>());
        services.AddSingleton<ToolRegistry>();
        return services;
    }
}
