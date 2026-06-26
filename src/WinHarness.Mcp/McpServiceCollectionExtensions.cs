using Microsoft.Extensions.DependencyInjection;
using WinHarness.Tools;

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
        services.AddSingleton<IMcpClientManager, McpClientManager>();
        services.AddSingleton<McpToolProvider>();
        services.AddSingleton<IToolProvider>(static provider => provider.GetRequiredService<McpToolProvider>());
        return services;
    }
}
