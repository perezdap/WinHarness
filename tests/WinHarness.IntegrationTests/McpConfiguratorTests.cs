using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class McpConfiguratorTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "WinHarnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AddStdioServerPersistsCommandArgumentsAndEnvironment()
    {
        ConfigStore store = new(_directory);
        McpConfigurator configurator = new(store);

        McpServerOptions server = await configurator.AddStdioServerAsync(
            "filesystem",
            "filesystem-mcp-server.exe",
            ["--root", "C:\\src"],
            "C:\\tools",
            new Dictionary<string, string?> { ["MCP_LOG"] = "debug" },
            enabled: true,
            startupTimeoutSeconds: 12,
            CancellationToken.None);

        Assert.AreEqual("stdio", server.Transport);
        Assert.AreEqual("filesystem-mcp-server.exe", server.Command);
        CollectionAssert.AreEqual(new[] { "--root", "C:\\src" }, server.Arguments);
        Assert.AreEqual("debug", server.Environment["MCP_LOG"]);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(1, saved.McpServers.Count);
        Assert.AreEqual("filesystem", saved.McpServers[0].Id);
    }

    [TestMethod]
    public async Task AddHttpServerPersistsEndpointHeadersAndTransport()
    {
        ConfigStore store = new(_directory);
        McpConfigurator configurator = new(store);

        McpServerOptions server = await configurator.AddHttpServerAsync(
            "remote",
            "sse",
            "https://example.com/mcp",
            new Dictionary<string, string> { ["Authorization"] = "Bearer token" },
            enabled: false,
            startupTimeoutSeconds: 3,
            CancellationToken.None);

        Assert.AreEqual("sse", server.Transport);
        Assert.AreEqual("https://example.com/mcp", server.Endpoint);
        Assert.AreEqual("Bearer token", server.Headers["Authorization"]);
        Assert.IsFalse(server.Enabled);
        Assert.AreEqual(string.Empty, server.Command);
    }

    [TestMethod]
    public async Task EnableDisableAndRemoveMutateExistingServer()
    {
        ConfigStore store = new(_directory);
        McpConfigurator configurator = new(store);

        await configurator.AddHttpServerAsync(
            "remote",
            "http",
            "https://example.com/mcp",
            new Dictionary<string, string>(),
            enabled: false,
            startupTimeoutSeconds: 30,
            CancellationToken.None);

        McpServerOptions enabled = await configurator.SetEnabledAsync("remote", true, CancellationToken.None);
        Assert.IsTrue(enabled.Enabled);

        McpServerOptions disabled = await configurator.SetEnabledAsync("remote", false, CancellationToken.None);
        Assert.IsFalse(disabled.Enabled);

        await configurator.RemoveServerAsync("remote", CancellationToken.None);
        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(0, saved.McpServers.Count);
    }
}
