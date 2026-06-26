using Microsoft.Extensions.DependencyInjection;

namespace WinHarness.Platform;

/// <summary>
/// Windows platform service registration.
/// </summary>
public static class PlatformServiceCollectionExtensions
{
    /// <summary>
    /// Adds platform services.
    /// </summary>
    public static IServiceCollection AddWinHarnessPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IAnsiConsoleConfigurator, WindowsAnsiConsoleConfigurator>();
        services.AddSingleton<ILongPathService, WindowsLongPathService>();
        services.AddSingleton<ICommandExecutor, CapturedCommandExecutor>();
        return services;
    }
}
