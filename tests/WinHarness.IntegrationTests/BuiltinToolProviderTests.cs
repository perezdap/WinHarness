using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Tools;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class BuiltinToolProviderTests
{
    [TestMethod]
    public async Task WriteReadAndEditFile()
    {
        string root = CreateTempDirectory();
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ITool write = tools.Single(static tool => tool.Name == "write_file");
        ITool read = tools.Single(static tool => tool.Name == "read_file");
        ITool edit = tools.Single(static tool => tool.Name == "edit_file");

        ToolResult writeResult = await write.ExecuteAsync(new ToolInvocation("write_file", Json("""{"path":"notes.txt","content":"hello","overwrite":false}""")), CancellationToken.None);
        ToolResult editResult = await edit.ExecuteAsync(new ToolInvocation("edit_file", Json("""{"path":"notes.txt","oldText":"hello","newText":"hello WinHarness"}""")), CancellationToken.None);
        ToolResult readResult = await read.ExecuteAsync(new ToolInvocation("read_file", Json("""{"path":"notes.txt"}""")), CancellationToken.None);

        Assert.IsTrue(writeResult.Succeeded);
        Assert.IsTrue(editResult.Succeeded);
        Assert.AreEqual("hello WinHarness", readResult.Content);
    }

    [TestMethod]
    public async Task GlobAndGrepReturnExpectedMatches()
    {
        string root = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "sample.cs"), "namespace WinHarness;");

        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult globResult = await tools.Single(static tool => tool.Name == "glob")
            .ExecuteAsync(new ToolInvocation("glob", Json("""{"pattern":"*.cs"}""")), CancellationToken.None);
        ToolResult grepResult = await tools.Single(static tool => tool.Name == "grep")
            .ExecuteAsync(new ToolInvocation("grep", Json("""{"pattern":"WinHarness","filePattern":"*.cs"}""")), CancellationToken.None);

        StringAssert.Contains(globResult.Content, "sample.cs");
        StringAssert.Contains(grepResult.Content, "namespace WinHarness;");
    }

    [TestMethod]
    public async Task RunCommandCapturesOutput()
    {
        string root = CreateTempDirectory();
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        string json = OperatingSystem.IsWindows()
            ? """{"command":"cmd.exe","arguments":["/c","echo hello"],"timeoutSeconds":10}"""
            : """{"command":"/bin/sh","arguments":["-c","echo hello"],"timeoutSeconds":10}""";

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(new ToolInvocation("run_command", Json(json)), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "hello");
    }

    [TestMethod]
    public async Task RejectsSiblingPathEscape()
    {
        string parent = Path.Combine(Path.GetTempPath(), "WinHarnessTests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, "root");
        string sibling = Path.Combine(parent, "root-sibling");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "secret.txt"), "secret");

        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);
        ITool read = tools.Single(static tool => tool.Name == "read_file");

        InvalidOperationException? exception = null;
        try
        {
            _ = await read.ExecuteAsync(
                new ToolInvocation("read_file", Json("""{"path":"../root-sibling/secret.txt"}""")),
                CancellationToken.None);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "escapes the workspace");
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinHarnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
