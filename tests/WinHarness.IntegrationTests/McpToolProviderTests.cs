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
