using System.IO.Enumeration;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinHarness.Platform;

namespace WinHarness.Tools;

/// <summary>
/// Provides built-in tools.
/// </summary>
public sealed class BuiltinToolProvider : IToolProvider
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ICommandExecutor _commandExecutor;
    private readonly ILongPathService _longPathService;
    private readonly string _workspaceRoot;

    /// <summary>
    /// Creates a built-in tool provider rooted at the current directory.
    /// </summary>
    public BuiltinToolProvider()
        : this(Environment.CurrentDirectory, new LocalCapturedCommandExecutor(), new PassThroughLongPathService())
    {
    }

    /// <summary>
    /// Creates a built-in tool provider rooted at the current directory.
    /// </summary>
    public BuiltinToolProvider(ICommandExecutor commandExecutor)
        : this(Environment.CurrentDirectory, commandExecutor, new PassThroughLongPathService())
    {
    }

    /// <summary>
    /// Creates a built-in tool provider.
    /// </summary>
    public BuiltinToolProvider(string workspaceRoot)
        : this(workspaceRoot, new LocalCapturedCommandExecutor(), new PassThroughLongPathService())
    {
    }

    /// <summary>
    /// Creates a built-in tool provider.
    /// </summary>
    public BuiltinToolProvider(string workspaceRoot, ICommandExecutor commandExecutor)
        : this(workspaceRoot, commandExecutor, new PassThroughLongPathService())
    {
    }

    /// <summary>
    /// Creates a built-in tool provider.
    /// </summary>
    public BuiltinToolProvider(
        string workspaceRoot,
        ICommandExecutor commandExecutor,
        ILongPathService longPathService)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _commandExecutor = commandExecutor;
        _longPathService = longPathService;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ITool> tools =
        [
            new ReadFileTool(_workspaceRoot, _longPathService),
            new WriteFileTool(_workspaceRoot, _longPathService),
            new EditFileTool(_workspaceRoot, _longPathService),
            new RunCommandTool(_workspaceRoot, _commandExecutor, _longPathService),
            new GlobTool(_workspaceRoot, _longPathService),
            new GrepTool(_workspaceRoot, _longPathService)
        ];

        return ValueTask.FromResult(tools);
    }

    private abstract class BuiltinTool : ITool
    {
        private readonly ILongPathService _longPathService;

        protected BuiltinTool(
            string workspaceRoot,
            ILongPathService longPathService,
            string name,
            string description,
            string schema)
        {
            WorkspaceRoot = workspaceRoot;
            _longPathService = longPathService;
            Name = name;
            Description = description;
            InputSchema = JsonDocument.Parse(schema).RootElement.Clone();
        }

        protected string WorkspaceRoot { get; }

        public string Name { get; }

        public string Description { get; }

        public JsonElement InputSchema { get; }

        public abstract ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);

        protected string ResolveWorkspacePath(JsonElement arguments, string propertyName = "path")
        {
            string relativePath = RequireString(arguments, propertyName);
            string fullPath = Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath));
            string relativeToWorkspace = Path.GetRelativePath(WorkspaceRoot, fullPath);

            if (Path.IsPathFullyQualified(relativeToWorkspace) ||
                relativeToWorkspace == ".." ||
                relativeToWorkspace.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativeToWorkspace.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path '{relativePath}' escapes the workspace.");
            }

            return fullPath;
        }

        protected string NormalizeForIo(string path)
        {
            return _longPathService.Normalize(path);
        }

        protected static string RequireString(JsonElement arguments, string propertyName)
        {
            if (!arguments.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"Missing string property '{propertyName}'.");
            }

            return value.GetString() ?? string.Empty;
        }

        protected static int OptionalInt32(JsonElement arguments, string propertyName, int defaultValue)
        {
            return arguments.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int parsed)
                ? parsed
                : defaultValue;
        }

        protected static bool OptionalBoolean(JsonElement arguments, string propertyName, bool defaultValue)
        {
            return arguments.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : defaultValue;
        }

        protected static async ValueTask AtomicWriteAllTextAsync(
            string path,
            string content,
            Encoding encoding,
            CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Path has no parent directory.");
            string tempPath = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            await File.WriteAllTextAsync(tempPath, content, encoding, cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                File.Delete(tempPath);
                throw;
            }
        }
    }

    private sealed class ReadFileTool : BuiltinTool
    {
        public ReadFileTool(string workspaceRoot, ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "read_file", "Read a UTF-8 text file.", """{"type":"object","properties":{"path":{"type":"string"},"maxBytes":{"type":"integer"}},"required":["path"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            int maxBytes = OptionalInt32(invocation.Arguments, "maxBytes", 256 * 1024);

            byte[] bytes = await File.ReadAllBytesAsync(NormalizeForIo(path), cancellationToken).ConfigureAwait(false);
            if (bytes.Length > maxBytes)
            {
                return new ToolResult(false, $"File exceeds maxBytes ({bytes.Length} > {maxBytes}).", "file_too_large");
            }

            return new ToolResult(true, Encoding.UTF8.GetString(bytes));
        }
    }

    private sealed class WriteFileTool : BuiltinTool
    {
        public WriteFileTool(string workspaceRoot, ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "write_file", "Write a UTF-8 text file.", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"overwrite":{"type":"boolean"},"createDirectories":{"type":"boolean"}},"required":["path","content"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            string content = RequireString(invocation.Arguments, "content");
            bool overwrite = OptionalBoolean(invocation.Arguments, "overwrite", false);
            bool createDirectories = OptionalBoolean(invocation.Arguments, "createDirectories", false);

            string? parent = Path.GetDirectoryName(path);
            if (parent is not null && !Directory.Exists(NormalizeForIo(parent)))
            {
                if (!createDirectories)
                {
                    return new ToolResult(false, "Parent directory does not exist.", "parent_missing");
                }

                Directory.CreateDirectory(NormalizeForIo(parent));
            }

            string normalizedPath = NormalizeForIo(path);
            if (File.Exists(normalizedPath) && !overwrite)
            {
                return new ToolResult(false, "File exists and overwrite is false.", "file_exists");
            }

            await AtomicWriteAllTextAsync(normalizedPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, $"Wrote {content.Length} characters.");
        }
    }

    private sealed class EditFileTool : BuiltinTool
    {
        public EditFileTool(string workspaceRoot, ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "edit_file", "Replace exact text in a file.", """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"},"expectedOccurrences":{"type":"integer"},"dryRun":{"type":"boolean"}},"required":["path","oldText","newText"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            string oldText = RequireString(invocation.Arguments, "oldText");
            string newText = RequireString(invocation.Arguments, "newText");
            int expectedOccurrences = OptionalInt32(invocation.Arguments, "expectedOccurrences", 1);
            bool dryRun = OptionalBoolean(invocation.Arguments, "dryRun", false);

            string normalizedPath = NormalizeForIo(path);
            string content = await File.ReadAllTextAsync(normalizedPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            int occurrences = CountOccurrences(content, oldText);
            if (occurrences != expectedOccurrences)
            {
                return new ToolResult(false, $"Expected {expectedOccurrences} occurrence(s), found {occurrences}.", "occurrence_mismatch");
            }

            string updated = content.Replace(oldText, newText, StringComparison.Ordinal);
            if (dryRun)
            {
                return new ToolResult(true, $"Would replace {occurrences} occurrence(s). Updated length: {updated.Length} characters.");
            }

            await AtomicWriteAllTextAsync(normalizedPath, updated, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, $"Replaced {occurrences} occurrence(s).");
        }

        private static int CountOccurrences(string content, string oldText)
        {
            if (oldText.Length == 0)
            {
                return 0;
            }

            int count = 0;
            int index = 0;
            while ((index = content.IndexOf(oldText, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += oldText.Length;
            }

            return count;
        }
    }

    private sealed class RunCommandTool : BuiltinTool
    {
        private readonly ICommandExecutor _commandExecutor;

        public RunCommandTool(
            string workspaceRoot,
            ICommandExecutor commandExecutor,
            ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "run_command", "Run a command with captured output by default.", """{"type":"object","properties":{"command":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer"},"maxOutputBytes":{"type":"integer"},"mode":{"type":"string","enum":["captured","interactive"]}},"required":["command"]}""")
        {
            _commandExecutor = commandExecutor;
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string mode = invocation.Arguments.TryGetProperty("mode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString() ?? "captured"
                : "captured";

            CommandExecutionMode executionMode = string.Equals(mode, "interactive", StringComparison.OrdinalIgnoreCase)
                ? CommandExecutionMode.Interactive
                : CommandExecutionMode.Captured;

            List<string> commandArguments = [];
            if (invocation.Arguments.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement argument in arguments.EnumerateArray())
                {
                    commandArguments.Add(argument.GetString() ?? string.Empty);
                }
            }

            CommandRequest request = new(
                FileName: RequireString(invocation.Arguments, "command"),
                Arguments: commandArguments,
                WorkingDirectory: invocation.Arguments.TryGetProperty("workingDirectory", out JsonElement workingDirectory) && workingDirectory.ValueKind == JsonValueKind.String
                    ? NormalizeForIo(ResolveWorkspacePath(invocation.Arguments, "workingDirectory"))
                    : WorkspaceRoot,
                Mode: executionMode,
                Timeout: TimeSpan.FromSeconds(OptionalInt32(invocation.Arguments, "timeoutSeconds", 60)));

            CommandResult result = await _commandExecutor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            string content = string.Concat("mode: ", result.Mode, Environment.NewLine, "exit_code: ", result.ExitCode, Environment.NewLine, "stdout:", Environment.NewLine, result.StandardOutput, Environment.NewLine, "stderr:", Environment.NewLine, result.StandardError);
            content = Truncate(content, OptionalInt32(invocation.Arguments, "maxOutputBytes", 128 * 1024));
            return new ToolResult(
                result.ExitCode == 0,
                content,
                result.ExitCode == 0 ? null : "nonzero_exit",
                new Dictionary<string, string>
                {
                    ["command.mode"] = result.Mode.ToString(),
                    ["command.exit_code"] = result.ExitCode.ToString(CultureInfo.InvariantCulture)
                });
        }

        private static string Truncate(string content, int maxOutputBytes)
        {
            if (Encoding.UTF8.GetByteCount(content) <= maxOutputBytes)
            {
                return content;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(content);
            return Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Max(0, maxOutputBytes))) + Environment.NewLine + "[output truncated]";
        }
    }

    private sealed class LocalCapturedCommandExecutor : ICommandExecutor
    {
        public async ValueTask<CommandResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
        {
            if (request.Mode == CommandExecutionMode.Interactive)
            {
                return new CommandResult(1, string.Empty, "Interactive command execution requires the Windows platform executor.", CommandExecutionMode.Interactive);
            }

            System.Diagnostics.ProcessStartInfo startInfo = new()
            {
                FileName = request.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = request.WorkingDirectory
            };

            foreach (string argument in request.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start process.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                string timedOutStdout = await stdoutTask.ConfigureAwait(false);
                string timedOutStderr = await stderrTask.ConfigureAwait(false);
                return new CommandResult(1, timedOutStdout, timedOutStderr + Environment.NewLine + "Process timed out.", CommandExecutionMode.Captured);
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return new CommandResult(process.ExitCode, stdout, stderr, CommandExecutionMode.Captured);
        }
    }

    private sealed class GlobTool : BuiltinTool
    {
        public GlobTool(string workspaceRoot, ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "glob", "List files matching a glob pattern.", """{"type":"object","properties":{"pattern":{"type":"string"},"maxResults":{"type":"integer"}},"required":["pattern"]}""")
        {
        }

        public override ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string pattern = RequireString(invocation.Arguments, "pattern");
            int maxResults = OptionalInt32(invocation.Arguments, "maxResults", 200);

            List<string> results = [];
            foreach (string file in Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(WorkspaceRoot, file).Replace('\\', '/');
                if (FileSystemName.MatchesSimpleExpression(pattern, relative, ignoreCase: true))
                {
                    results.Add(relative);
                    if (results.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            return ValueTask.FromResult(new ToolResult(true, string.Join(Environment.NewLine, results)));
        }
    }

    private sealed class GrepTool : BuiltinTool
    {
        public GrepTool(string workspaceRoot, ILongPathService longPathService)
            : base(workspaceRoot, longPathService, "grep", "Search text files.", """{"type":"object","properties":{"pattern":{"type":"string"},"filePattern":{"type":"string"},"maxResults":{"type":"integer"},"maxFileBytes":{"type":"integer"}},"required":["pattern"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Regex regex = new(RequireString(invocation.Arguments, "pattern"), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            string filePattern = invocation.Arguments.TryGetProperty("filePattern", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "*"
                : "*";
            int maxResults = OptionalInt32(invocation.Arguments, "maxResults", 100);
            int maxFileBytes = OptionalInt32(invocation.Arguments, "maxFileBytes", 1024 * 1024);

            List<string> results = [];
            foreach (string file in Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(WorkspaceRoot, file).Replace('\\', '/');
                if (!FileSystemName.MatchesSimpleExpression(filePattern, relative, ignoreCase: true))
                {
                    continue;
                }

                string normalizedFile = NormalizeForIo(file);
                FileInfo info = new(normalizedFile);
                if (info.Length > maxFileBytes)
                {
                    continue;
                }

                if (await IsBinaryFileAsync(normalizedFile, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string[] lines = await File.ReadAllLinesAsync(normalizedFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < lines.Length; index++)
                {
                    if (regex.IsMatch(lines[index]))
                    {
                        results.Add($"{relative}:{index + 1}:{lines[index]}");
                        if (results.Count >= maxResults)
                        {
                            return new ToolResult(true, string.Join(Environment.NewLine, results));
                        }
                    }
                }
            }

            return new ToolResult(true, string.Join(Environment.NewLine, results));
        }

        private static async ValueTask<bool> IsBinaryFileAsync(string file, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024];
            await using FileStream stream = File.OpenRead(file);
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.AsSpan(0, bytesRead).Contains((byte)0);
        }
    }

    private sealed class PassThroughLongPathService : ILongPathService
    {
        public string Normalize(string path)
        {
            return path;
        }
    }
}
