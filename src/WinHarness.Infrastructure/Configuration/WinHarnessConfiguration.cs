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
        ValidateNoInlineSecrets(configuration);
        WinHarnessOptions options = new();
        configuration.Bind(options);
        WinHarnessOptionsValidator.Validate(options);
        services.AddSingleton(options);
        return services;
    }

    private static void ValidateNoInlineSecrets(IConfiguration configuration)
    {
        foreach (IConfigurationSection section in configuration.AsEnumerable().Select(pair => configuration.GetSection(pair.Key)))
        {
            if (section.Value is null)
            {
                continue;
            }

            string key = section.Path;
            if (IsAllowedSecretReferenceKey(key))
            {
                continue;
            }

            if (LooksSecretBearingKey(key))
            {
                throw new InvalidOperationException($"Configuration key '{key}' appears to contain a secret. Store secrets in Windows Credential Manager and reference them with credentialName.");
            }
        }
    }

    private static bool IsAllowedSecretReferenceKey(string key)
    {
        return key.EndsWith(":credentialName", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "credentialName", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksSecretBearingKey(string key)
    {
        string[] secretTerms = ["apikey", "api_key", "secret", "token", "password"];
        string compactKey = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        foreach (string term in secretTerms)
        {
            string compactTerm = term.Replace("_", string.Empty, StringComparison.Ordinal);
            if (compactKey.Contains(compactTerm, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
