using WinHarness.Configuration;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Validates WinHarness options without reflection.
/// </summary>
public static class WinHarnessOptionsValidator
{
    /// <summary>
    /// Validates options and throws when invalid.
    /// </summary>
    public static void Validate(WinHarnessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        HashSet<string> providerIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProviderOptions provider in options.Providers)
        {
            RequireNonEmpty(provider.Id, "Provider id is required.");
            RequireNonEmpty(provider.Kind, $"Provider '{provider.Id}' kind is required.");

            if (!string.Equals(provider.Kind, "openai-compatible", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Provider '{provider.Id}' uses unsupported kind '{provider.Kind}'. v0.1 supports only openai-compatible.");
            }

            if (!providerIds.Add(provider.Id))
            {
                throw new InvalidOperationException($"Duplicate provider id '{provider.Id}'.");
            }

            if (provider.BaseUrl is not null && !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Provider '{provider.Id}' baseUrl is not an absolute URI.");
            }

            HashSet<string> modelIds = new(StringComparer.OrdinalIgnoreCase);
            foreach (ModelOptions model in provider.Models)
            {
                RequireNonEmpty(model.Id, $"Provider '{provider.Id}' has a model without an id.");
                RequireNonEmpty(model.ProviderModelId, $"Model '{model.Id}' is missing providerModelId.");

                if (!modelIds.Add(model.Id))
                {
                    throw new InvalidOperationException($"Provider '{provider.Id}' has duplicate model id '{model.Id}'.");
                }
            }
        }

        if (options.DefaultProvider.Length > 0 && !providerIds.Contains(options.DefaultProvider))
        {
            throw new InvalidOperationException($"Default provider '{options.DefaultProvider}' is not configured.");
        }
    }

    private static void RequireNonEmpty(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
    }
}
