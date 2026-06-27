using ModelContextProtocol.Client;
using WinHarness.Configuration;

namespace WinHarness.Mcp;

/// <summary>
/// Manages stdio MCP clients.
/// </summary>
public interface IMcpClientManager : IAsyncDisposable
{
    /// <summary>
    /// Gets or creates a connected MCP client.
    /// </summary>
    ValueTask<McpClient> GetClientAsync(McpServerOptions server, CancellationToken cancellationToken);
}

/// <summary>
/// Default MCP client manager.
/// </summary>
public sealed class McpClientManager : IMcpClientManager
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public async ValueTask<McpClient> GetClientAsync(McpServerOptions server, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(server.Id, out McpClient? existing))
            {
                if (!existing.Completion.IsCompleted)
                {
                    return existing;
                }

                await existing.DisposeAsync().ConfigureAwait(false);
                _clients.Remove(server.Id);
            }

            using CancellationTokenSource startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, server.StartupTimeoutSeconds)));

            IClientTransport transport = CreateTransport(server);

            McpClient client = await McpClient.CreateAsync(transport, cancellationToken: startupTimeout.Token)
                .ConfigureAwait(false);
            _clients.Add(server.Id, client);
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IClientTransport CreateTransport(McpServerOptions server)
    {
        if (IsStdioTransport(server.Transport))
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Id,
                Command = server.Command,
                Arguments = [.. server.Arguments],
                WorkingDirectory = server.WorkingDirectory,
                EnvironmentVariables = server.Environment.Count == 0 ? null : server.Environment,
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            });
        }

        HttpTransportMode mode = string.Equals(server.Transport, "sse", StringComparison.OrdinalIgnoreCase)
            ? HttpTransportMode.Sse
            : HttpTransportMode.StreamableHttp;

        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Id,
            Endpoint = new Uri(server.Endpoint ?? throw new InvalidOperationException($"MCP server '{server.Id}' endpoint is required.")),
            TransportMode = mode,
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(1, server.StartupTimeoutSeconds)),
            AdditionalHeaders = server.Headers.Count == 0 ? null : server.Headers
        });
    }

    private static bool IsStdioTransport(string transport)
    {
        return string.IsNullOrWhiteSpace(transport) ||
            string.Equals(transport, "stdio", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (McpClient client in _clients.Values)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            _clients.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
