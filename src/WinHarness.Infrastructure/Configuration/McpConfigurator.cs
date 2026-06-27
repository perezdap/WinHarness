using WinHarness.Configuration;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Owns all mutations to MCP server configuration. Callers describe the desired
/// change; this type performs the read-modify-write against
/// <see cref="ConfigStore"/>. It never prompts and never writes to the console,
/// so it is reusable from any front end.
/// </summary>
public sealed class McpConfigurator
{
    private readonly ConfigStore _store;

    /// <summary>
    /// Creates a configurator.
    /// </summary>
    public McpConfigurator(ConfigStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Adds or replaces a stdio MCP server. Returns the persisted definition.
    /// </summary>
    public async ValueTask<McpServerOptions> AddStdioServerAsync(
        string id,
        string command,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        bool enabled,
        int startupTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        McpServerOptions server = GetOrCreate(options, id);

        server.Transport = "stdio";
        server.Command = command;
        server.Arguments = [.. arguments];
        server.WorkingDirectory = workingDirectory;
        server.Environment = new Dictionary<string, string?>(environment);
        server.Endpoint = null;
        server.Headers = [];
        server.Enabled = enabled;
        server.StartupTimeoutSeconds = NormalizeTimeout(startupTimeoutSeconds);

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return server;
    }

    /// <summary>
    /// Adds or replaces an HTTP or SSE MCP server. Returns the persisted
    /// definition. <paramref name="transport"/> must be "http" or "sse".
    /// </summary>
    public async ValueTask<McpServerOptions> AddHttpServerAsync(
        string id,
        string transport,
        string endpoint,
        IReadOnlyDictionary<string, string> headers,
        bool enabled,
        int startupTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (!string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(transport, "sse", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Transport '{transport}' is not an HTTP transport. Use 'http' or 'sse'.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Endpoint '{endpoint}' must be an absolute HTTP or HTTPS URI.");
        }

        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        McpServerOptions server = GetOrCreate(options, id);

        server.Transport = transport.ToLowerInvariant();
        server.Endpoint = endpoint;
        server.Headers = new Dictionary<string, string>(headers);
        server.Command = string.Empty;
        server.Arguments = [];
        server.WorkingDirectory = null;
        server.Environment = [];
        server.Enabled = enabled;
        server.StartupTimeoutSeconds = NormalizeTimeout(startupTimeoutSeconds);

        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return server;
    }

    /// <summary>
    /// Removes an MCP server by id.
    /// </summary>
    public async ValueTask RemoveServerAsync(string id, CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        McpServerOptions server = options.McpServers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"MCP server '{id}' is not configured.");

        options.McpServers.Remove(server);
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables or disables an MCP server. Returns the updated definition.
    /// </summary>
    public async ValueTask<McpServerOptions> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        McpServerOptions server = options.McpServers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"MCP server '{id}' is not configured.");

        server.Enabled = enabled;
        await _store.SaveAsync(options, cancellationToken).ConfigureAwait(false);
        return server;
    }

    private static McpServerOptions GetOrCreate(WinHarnessOptions options, string id)
    {
        McpServerOptions? existing = options.McpServers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        McpServerOptions server = new() { Id = id };
        options.McpServers.Add(server);
        return server;
    }

    private static int NormalizeTimeout(int startupTimeoutSeconds)
    {
        return startupTimeoutSeconds <= 0 ? 30 : startupTimeoutSeconds;
    }
}
