using System.Globalization;
using System.Text.RegularExpressions;
using WinHarness.Configuration;
using WinHarness.Providers;

namespace WinHarness.Cli.Configuration;

/// <summary>
/// CLI input validation helpers.
/// </summary>
internal static class CliValidation
{
    /// <summary>
    /// Builds a case-insensitive wildcard predicate over model id and
    /// providerModelId. Supports '*' (any run) in the pattern; a pattern
    /// without '*' matches as a substring.
    /// </summary>
    public static Func<ModelOptions, bool> CreateModelFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return static _ => true;
        }

        string regexPattern = pattern.Contains('*')
            ? "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$"
            : Regex.Escape(pattern);
        Regex regex = new(
            regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        return model => regex.IsMatch(model.Id) || regex.IsMatch(model.ProviderModelId);
    }

    public static void ValidateCredentialTargetName(string targetName)
    {
        if (!targetName.StartsWith("WinHarness:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Credential target names must start with 'WinHarness:'.");
        }
    }
}
