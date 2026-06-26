using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<BuiltinToolProvider>();
        services.AddSingleton<IToolProvider>(static provider => provider.GetRequiredService<BuiltinToolProvider>());
        return services;
    }
}
