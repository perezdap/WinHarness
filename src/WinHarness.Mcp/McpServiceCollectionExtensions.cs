using Microsoft.Extensions.DependencyInjection;

namespace WinHarness.Mcp;

/// <summary>
/// MCP service registration.
/// </summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// Adds MCP services.
    /// </summary>
    public static IServiceCollection AddWinHarnessMcp(this IServiceCollection services)
    {
        return services;
    }
}
