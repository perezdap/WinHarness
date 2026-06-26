using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WinHarness.Configuration;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Configuration helpers for WinHarness.
/// </summary>
public static class WinHarnessConfiguration
{
    /// <summary>
    /// Gets the per-user WinHarness configuration directory.
    /// </summary>
    public static string GetConfigurationDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "WinHarness");
    }

    /// <summary>
    /// Adds WinHarness configuration files.
    /// </summary>
    public static IConfigurationBuilder AddWinHarnessConfiguration(this IConfigurationBuilder builder, string? configurationDirectory = null)
    {
        string directory = configurationDirectory ?? GetConfigurationDirectory();
        return builder
            .AddJsonFile(Path.Combine(directory, "config.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(directory, "model-capabilities.json"), optional: true, reloadOnChange: false);
    }

    /// <summary>
    /// Registers bound WinHarness options.
    /// </summary>
    public static IServiceCollection AddWinHarnessOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        WinHarnessOptions options = new();
        configuration.Bind(options);
        WinHarnessOptionsValidator.Validate(options);
        services.AddSingleton(options);
        return services;
    }
}
