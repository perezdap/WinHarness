using Microsoft.Extensions.Logging;

namespace WinHarness.Mcp;

internal static partial class LoggerExtensions
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "MCP server '{McpServerId}' is unavailable; skipping its tools. {Error}")]
    public static partial void McpServerUnavailable(this ILogger logger, string mcpServerId, string error);
}
