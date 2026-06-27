using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using WinHarness.Configuration;
using WinHarness.Tools;

namespace WinHarness.Mcp;

/// <summary>
/// Exposes MCP tools through the WinHarness tool interface.
/// </summary>
public sealed class McpToolProvider : IToolProvider
{
    private readonly WinHarnessOptions _options;
    private readonly IMcpClientManager _clientManager;
    private readonly ILogger<McpToolProvider> _logger;

    /// <summary>
    /// Creates an MCP tool provider.
    /// </summary>
    public McpToolProvider(WinHarnessOptions options, IMcpClientManager clientManager)
        : this(options, clientManager, NullLogger<McpToolProvider>.Instance)
    {
    }

    /// <summary>
    /// Creates an MCP tool provider.
    /// </summary>
    public McpToolProvider(WinHarnessOptions options, IMcpClientManager clientManager, ILogger<McpToolProvider> logger)
    {
        _options = options;
        _clientManager = clientManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        List<ITool> tools = [];
        foreach (McpServerOptions server in _options.McpServers)
        {
            if (!server.Enabled)
            {
                continue;
            }

            // A single unavailable or misbehaving MCP server must not take down the whole
            // session. Isolate per-server failures so the remaining servers and built-in
            // tools stay usable; only honor genuine cancellation.
            try
            {
                McpClient client = await _clientManager.GetClientAsync(server, cancellationToken).ConfigureAwait(false);
                IList<McpClientTool> discovered = await client.ListToolsAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                foreach (McpClientTool tool in discovered)
                {
                    tools.Add(new McpTool(server.Id, client, tool));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.McpServerUnavailable(server.Id, ex.Message);
            }
        }

        return tools;
    }

    private sealed class McpTool : ITool
    {
        private readonly McpClient _client;

        public McpTool(string serverId, McpClient client, McpClientTool tool)
        {
            _client = client;
            Name = $"{serverId}.{tool.Name}";
            McpName = tool.Name;
            Description = tool.Description;
            InputSchema = tool.JsonSchema;
        }

        private string McpName { get; }

        public string Name { get; }

        public string Description { get; }

        public JsonElement InputSchema { get; }

        public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Dictionary<string, object?> arguments = ConvertArguments(invocation.Arguments);
            CallToolResult result = await _client.CallToolAsync(McpName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            string content = ExtractText(result);
            return new ToolResult(result.IsError is not true, content, result.IsError is true ? "mcp_tool_error" : null);
        }

        private static Dictionary<string, object?> ConvertArguments(JsonElement arguments)
        {
            Dictionary<string, object?> converted = new(StringComparer.Ordinal);
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return converted;
            }

            foreach (JsonProperty property in arguments.EnumerateObject())
            {
                converted[property.Name] = ConvertValue(property.Value);
            }

            return converted;
        }

        private static object? ConvertValue(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out long longValue) => longValue,
                JsonValueKind.Number when value.TryGetDouble(out double doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value.Clone()
            };
        }

        private static string ExtractText(CallToolResult result)
        {
            List<string> parts = [];
            foreach (ContentBlock block in result.Content)
            {
                if (block is TextContentBlock text)
                {
                    parts.Add(text.Text);
                }
                else
                {
                    parts.Add(block.Type);
                }
            }

            return string.Join(Environment.NewLine, parts);
        }
    }
}
