using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Platform;
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
    public async Task EditFileDryRunDoesNotChangeFile()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(path, "before");
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "edit_file")
            .ExecuteAsync(new ToolInvocation("edit_file", Json("""{"path":"notes.txt","oldText":"before","newText":"after","dryRun":true}""")), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("before", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task EditFileMatchesLfOldTextAgainstCrlfFile()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "code.txt");
        await File.WriteAllTextAsync(path, "line1\r\nline2\r\nline3\r\n");
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "edit_file")
            .ExecuteAsync(
                new ToolInvocation("edit_file", Json("""{"path":"code.txt","oldText":"line1\nline2","newText":"line1\nCHANGED"}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        // Replacement preserves the file's CRLF convention.
        Assert.AreEqual("line1\r\nCHANGED\r\nline3\r\n", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task EditFilePreservesCrlfWhenOldTextUsesCrlf()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "code.txt");
        await File.WriteAllTextAsync(path, "a\r\nb\r\nc\r\n");
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "edit_file")
            .ExecuteAsync(
                new ToolInvocation("edit_file", Json("""{"path":"code.txt","oldText":"a\r\nb","newText":"a\r\nB"}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("a\r\nB\r\nc\r\n", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task EditFileMatchesCrlfOldTextAgainstLfFile()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "code.txt");
        await File.WriteAllTextAsync(path, "a\nb\nc\n");
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "edit_file")
            .ExecuteAsync(
                new ToolInvocation("edit_file", Json("""{"path":"code.txt","oldText":"a\r\nb","newText":"a\r\nB"}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        // File was LF; replacement keeps LF.
        Assert.AreEqual("a\nB\nc\n", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task GrepSkipsFilesOverMaxFileBytes()
    {
        string root = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), "needle");
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "grep")
            .ExecuteAsync(new ToolInvocation("grep", Json("""{"pattern":"needle","filePattern":"*.txt","maxFileBytes":1}""")), CancellationToken.None);

        Assert.AreEqual(string.Empty, result.Content);
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
    public async Task RunCommandDeliversInputToStdin()
    {
        string root = CreateTempDirectory();
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        string json = OperatingSystem.IsWindows()
            ? """{"command":"cmd.exe","arguments":["/c","findstr .*"],"input":"hello\n","timeoutSeconds":10}"""
            : """{"command":"/bin/sh","arguments":["-c","read value; echo got:$value"],"input":"hello\n","timeoutSeconds":10}""";

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(new ToolInvocation("run_command", Json(json)), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Content, "hello");
    }

    [TestMethod]
    public async Task RunCommandReturnsGracefulFailureForMissingExecutable()
    {
        string root = CreateTempDirectory();
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        // Mimics an agent emitting the Unix shell builtin "command -v firecrawl",
        // which previously surfaced as an unhandled Win32Exception.
        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(
                new ToolInvocation("run_command", Json("""{"command":"command","arguments":["-v","firecrawl"],"timeoutSeconds":10}""")),
                CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("nonzero_exit", result.ErrorCode);
        StringAssert.Contains(result.Content, "Failed to start 'command'");
        StringAssert.Contains(result.Content, "where.exe");
    }

    [TestMethod]
    public async Task RunCommandReturnsGracefulFailureForEscapingWorkingDirectory()
    {
        string root = CreateTempDirectory();
        BuiltinToolProvider provider = new(root);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);
        string escapedWorkingDirectory = JsonEncodedText.Encode(Path.GetTempPath()).ToString();
        string json = $$"""{"command":"dotnet","workingDirectory":"{{escapedWorkingDirectory}}","timeoutSeconds":10}""";

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(new ToolInvocation("run_command", Json(json)), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("invalid_arguments", result.ErrorCode);
        StringAssert.Contains(result.Content, "escapes the workspace");
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

    [TestMethod]
    public async Task FileToolsUseLongPathNormalization()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(path, "content");
        RecordingLongPathService longPathService = new();
        BuiltinToolProvider provider = new(root, new FakeCommandExecutor(), longPathService);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "read_file")
            .ExecuteAsync(new ToolInvocation("read_file", Json("""{"path":"notes.txt"}""")), CancellationToken.None);

        Assert.AreEqual("content", result.Content);
        Assert.IsTrue(longPathService.Paths.Any(candidate => candidate.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RunCommandSplitsFullCommandLineWhenArgumentsAreOmitted()
    {
        string root = CreateTempDirectory();
        FakeCommandExecutor executor = new();
        BuiltinToolProvider provider = new(root, executor);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(
                new ToolInvocation("run_command", Json("""{"command":"dotnet build \"WinHarness.sln\"","timeoutSeconds":10}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(executor.LastRequest);
        Assert.AreEqual("dotnet", executor.LastRequest!.FileName);
        CollectionAssert.AreEqual(new[] { "build", "WinHarness.sln" }, executor.LastRequest.Arguments.ToArray());
        Assert.IsNull(executor.LastRequest.StandardInput);
    }

    [TestMethod]
    public async Task RunCommandPassesInputToCommandRequest()
    {
        string root = CreateTempDirectory();
        FakeCommandExecutor executor = new();
        BuiltinToolProvider provider = new(root, executor);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(
                new ToolInvocation("run_command", Json("""{"command":"cmd.exe","arguments":["/c","set /p value=Enter:"],"input":"hello\n","timeoutSeconds":10}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(executor.LastRequest);
        Assert.AreEqual("hello\n", executor.LastRequest!.StandardInput);
    }

    [TestMethod]
    public async Task RunCommandKeepsSpacedCommandPathWhenArgumentsAreExplicit()
    {
        string root = CreateTempDirectory();
        FakeCommandExecutor executor = new();
        BuiltinToolProvider provider = new(root, executor);
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(
                new ToolInvocation("run_command", Json("""{"command":"C:\\Program Files\\tool.exe","arguments":["--version"],"timeoutSeconds":10}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(executor.LastRequest);
        Assert.AreEqual(@"C:\Program Files\tool.exe", executor.LastRequest!.FileName);
        CollectionAssert.AreEqual(new[] { "--version" }, executor.LastRequest.Arguments.ToArray());
    }

    [TestMethod]
    public async Task RunCommandDoesNotLongPathPrefixWorkingDirectory()
    {
        // Regression: the \\?\ prefix from long-path normalization is not a valid
        // process working directory. MSBuild running under \\?\C:\... fails to
        // resolve Directory.Build.props imports (the '?' gets escaped to %3f).
        string root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        FakeCommandExecutor executor = new();
        BuiltinToolProvider provider = new(root, executor, new PrefixingLongPathService());
        IReadOnlyList<ITool> tools = await provider.ListToolsAsync(CancellationToken.None);

        ToolResult result = await tools.Single(static tool => tool.Name == "run_command")
            .ExecuteAsync(
                new ToolInvocation("run_command", Json("""{"command":"dotnet","arguments":["--info"],"workingDirectory":"sub","timeoutSeconds":10}""")),
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(executor.LastRequest);
        Assert.IsFalse(
            executor.LastRequest!.WorkingDirectory!.StartsWith(@"\\?\", StringComparison.Ordinal),
            $"Working directory must not carry the long-path prefix: {executor.LastRequest.WorkingDirectory}");
        Assert.AreEqual(Path.Combine(root, "sub"), executor.LastRequest.WorkingDirectory);
    }

    private sealed class PrefixingLongPathService : ILongPathService
    {
        public string Normalize(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath : @"\\?\" + fullPath;
        }
    }

    private sealed class RecordingLongPathService : ILongPathService
    {
        public List<string> Paths { get; } = [];

        public string Normalize(string path)
        {
            Paths.Add(path);
            return path;
        }
    }

    private sealed class FakeCommandExecutor : ICommandExecutor
    {
        public CommandRequest? LastRequest { get; private set; }

        public ValueTask<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(new CommandResult(0, string.Empty, string.Empty, request.Mode));
        }
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
