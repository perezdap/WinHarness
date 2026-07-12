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

            if (!IsSupportedProviderKind(provider.Kind))
            {
                throw new InvalidOperationException(
                    $"Provider '{provider.Id}' uses unsupported kind '{provider.Kind}'. Supported kinds: openai-compatible, anthropic-messages.");
            }

            if (!providerIds.Add(provider.Id))
            {
                throw new InvalidOperationException($"Duplicate provider id '{provider.Id}'.");
            }

            if (provider.BaseUrl is not null && !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Provider '{provider.Id}' baseUrl is not an absolute URI.");
            }

            if (provider.CredentialName is not null &&
                !provider.CredentialName.StartsWith("WinHarness:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Provider '{provider.Id}' credentialName must start with 'WinHarness:'.");
            }

            if (provider.Auth is { } auth)
            {
                bool isApiKey = string.Equals(auth.Scheme, "api-key", StringComparison.OrdinalIgnoreCase);
                bool isOAuth = string.Equals(auth.Scheme, "oauth", StringComparison.OrdinalIgnoreCase);
                if (!isApiKey && !isOAuth)
                {
                    throw new InvalidOperationException($"Provider '{provider.Id}' auth scheme '{auth.Scheme}' is not supported. Use api-key or oauth.");
                }

                if (isOAuth && string.IsNullOrWhiteSpace(auth.OAuthProvider))
                {
                    throw new InvalidOperationException($"Provider '{provider.Id}' uses the oauth scheme but is missing auth.oauthProvider.");
                }
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

        if (options.DefaultProvider.Length > 0 && options.DefaultModel.Length > 0)
        {
            ProviderOptions defaultProvider = options.Providers.First(provider =>
                string.Equals(provider.Id, options.DefaultProvider, StringComparison.OrdinalIgnoreCase));
            bool hasDefaultModel = defaultProvider.Models.Any(model =>
                string.Equals(model.Id, options.DefaultModel, StringComparison.OrdinalIgnoreCase));
            if (!hasDefaultModel)
            {
                throw new InvalidOperationException($"Default model '{options.DefaultModel}' is not configured for provider '{options.DefaultProvider}'.");
            }
        }

        HashSet<string> mcpServerIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (McpServerOptions server in options.McpServers)
        {
            RequireNonEmpty(server.Id, "MCP server id is required.");
            RequireNonEmpty(server.Transport, $"MCP server '{server.Id}' transport is required.");

            if (!mcpServerIds.Add(server.Id))
            {
                throw new InvalidOperationException($"Duplicate MCP server id '{server.Id}'.");
            }

            if (IsStdioTransport(server.Transport))
            {
                RequireNonEmpty(server.Command, $"MCP server '{server.Id}' command is required for stdio transport.");
            }
            else if (IsHttpTransport(server.Transport))
            {
                RequireNonEmpty(server.Endpoint ?? string.Empty, $"MCP server '{server.Id}' endpoint is required for {server.Transport} transport.");
                if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out Uri? endpoint) ||
                    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException($"MCP server '{server.Id}' endpoint must be an absolute HTTP or HTTPS URI.");
                }
            }
            else
            {
                throw new InvalidOperationException($"MCP server '{server.Id}' uses unsupported transport '{server.Transport}'. Supported values are stdio, http, and sse.");
            }
        }
    }

    private static bool IsSupportedProviderKind(string kind)
    {
        return string.Equals(kind, "openai-compatible", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "anthropic-messages", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStdioTransport(string transport)
    {
        return string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpTransport(string transport)
    {
        return string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transport, "sse", StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireNonEmpty(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
    }
}
