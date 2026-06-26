using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Mcp;
using WinHarness.Tools;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class McpToolProviderTests
{
    [TestMethod]
    public async Task ReportsServerStartupFailure()
    {
        WinHarnessOptions options = new();
        options.McpServers.Add(new McpServerOptions
        {
            Id = "missing",
            Command = OperatingSystem.IsWindows() ? "missing-winharness-mcp-server.exe" : "/missing-winharness-mcp-server",
            Enabled = true,
            StartupTimeoutSeconds = 1
        });

        await using McpClientManager manager = new();
        McpToolProvider provider = new(options, manager);

        Exception? exception = null;
        try
        {
            _ = await provider.ListToolsAsync(CancellationToken.None);
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task HonorsCancellationBeforeServerStartup()
    {
        WinHarnessOptions options = new();
        McpServerOptions server = new()
        {
            Id = "cancelled",
            Command = OperatingSystem.IsWindows() ? "missing-winharness-mcp-server.exe" : "/missing-winharness-mcp-server",
            Enabled = true,
            StartupTimeoutSeconds = 30
        };
        options.McpServers.Add(server);

        await using McpClientManager manager = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        OperationCanceledException? exception = null;
        try
        {
            _ = await manager.GetClientAsync(server, cancellation.Token);
        }
        catch (OperationCanceledException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task DiscoversAndCallsStdioMcpTool()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake MCP executable path is validated on the Windows CI runner.");
        }

        string serverPath = GetAotSpikeExecutablePath();
        if (!File.Exists(serverPath))
        {
            Assert.Inconclusive("The AOT spike executable was not built at the expected path.");
        }

        WinHarnessOptions options = new();
        options.McpServers.Add(new McpServerOptions
        {
            Id = "fake",
            Command = serverPath,
            Enabled = true
        });
        options.McpServers[0].Arguments.Add("mcp-server");

        await using McpClientManager manager = new();
        McpToolProvider provider = new(options, manager);

        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);
        ITool tool = tools.Single(static candidate => candidate.Name == "fake.spike_echo");

        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("fake.spike_echo", Json("""{"message":"hello mcp"}""")),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "hello mcp");
    }

    [TestMethod]
    public async Task NormalizesMcpToolErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The fake MCP executable path is validated on the Windows CI runner.");
        }

        string serverPath = GetAotSpikeExecutablePath();
        if (!File.Exists(serverPath))
        {
            Assert.Inconclusive("The AOT spike executable was not built at the expected path.");
        }

        WinHarnessOptions options = new();
        options.McpServers.Add(new McpServerOptions
        {
            Id = "fake",
            Command = serverPath,
            Enabled = true
        });
        options.McpServers[0].Arguments.Add("mcp-server");

        await using McpClientManager manager = new();
        McpToolProvider provider = new(options, manager);

        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);
        ITool tool = tools.Single(static candidate => candidate.Name == "fake.spike_fail");

        ToolResult result = await tool.ExecuteAsync(
            new ToolInvocation("fake.spike_fail", Json("""{"message":"expected failure"}""")),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("mcp_tool_error", result.ErrorCode);
        StringAssert.Contains(result.Content, "expected failure");
    }

    private static string GetAotSpikeExecutablePath()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(
            repoRoot,
            "spikes",
            "WinHarness.AotSpike",
            "bin",
            "Release",
            "net10.0",
            "win-x64",
            "winharness-aot-spike.exe");
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
