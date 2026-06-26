using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinHarness.Tools;

/// <summary>
/// Provides built-in tools.
/// </summary>
public sealed class BuiltinToolProvider : IToolProvider
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _workspaceRoot;

    /// <summary>
    /// Creates a built-in tool provider rooted at the current directory.
    /// </summary>
    public BuiltinToolProvider()
        : this(Environment.CurrentDirectory)
    {
    }

    /// <summary>
    /// Creates a built-in tool provider.
    /// </summary>
    public BuiltinToolProvider(string workspaceRoot)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ITool> tools =
        [
            new ReadFileTool(_workspaceRoot),
            new WriteFileTool(_workspaceRoot),
            new EditFileTool(_workspaceRoot),
            new RunCommandTool(_workspaceRoot),
            new GlobTool(_workspaceRoot),
            new GrepTool(_workspaceRoot)
        ];

        return ValueTask.FromResult(tools);
    }

    private abstract class BuiltinTool : ITool
    {
        protected BuiltinTool(string workspaceRoot, string name, string description, string schema)
        {
            WorkspaceRoot = workspaceRoot;
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

            if (!fullPath.StartsWith(WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path '{relativePath}' escapes the workspace.");
            }

            return fullPath;
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
    }

    private sealed class ReadFileTool : BuiltinTool
    {
        public ReadFileTool(string workspaceRoot)
            : base(workspaceRoot, "read_file", "Read a UTF-8 text file.", """{"type":"object","properties":{"path":{"type":"string"},"maxBytes":{"type":"integer"}},"required":["path"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            int maxBytes = OptionalInt32(invocation.Arguments, "maxBytes", 256 * 1024);

            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > maxBytes)
            {
                return new ToolResult(false, $"File exceeds maxBytes ({bytes.Length} > {maxBytes}).", "file_too_large");
            }

            return new ToolResult(true, Encoding.UTF8.GetString(bytes));
        }
    }

    private sealed class WriteFileTool : BuiltinTool
    {
        public WriteFileTool(string workspaceRoot)
            : base(workspaceRoot, "write_file", "Write a UTF-8 text file.", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"overwrite":{"type":"boolean"},"createDirectories":{"type":"boolean"}},"required":["path","content"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            string content = RequireString(invocation.Arguments, "content");
            bool overwrite = OptionalBoolean(invocation.Arguments, "overwrite", false);
            bool createDirectories = OptionalBoolean(invocation.Arguments, "createDirectories", false);

            string? parent = Path.GetDirectoryName(path);
            if (parent is not null && !Directory.Exists(parent))
            {
                if (!createDirectories)
                {
                    return new ToolResult(false, "Parent directory does not exist.", "parent_missing");
                }

                Directory.CreateDirectory(parent);
            }

            if (File.Exists(path) && !overwrite)
            {
                return new ToolResult(false, "File exists and overwrite is false.", "file_exists");
            }

            await File.WriteAllTextAsync(path, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            return new ToolResult(true, $"Wrote {content.Length} characters.");
        }
    }

    private sealed class EditFileTool : BuiltinTool
    {
        public EditFileTool(string workspaceRoot)
            : base(workspaceRoot, "edit_file", "Replace exact text in a file.", """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"},"expectedOccurrences":{"type":"integer"}},"required":["path","oldText","newText"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string path = ResolveWorkspacePath(invocation.Arguments);
            string oldText = RequireString(invocation.Arguments, "oldText");
            string newText = RequireString(invocation.Arguments, "newText");
            int expectedOccurrences = OptionalInt32(invocation.Arguments, "expectedOccurrences", 1);

            string content = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            int occurrences = CountOccurrences(content, oldText);
            if (occurrences != expectedOccurrences)
            {
                return new ToolResult(false, $"Expected {expectedOccurrences} occurrence(s), found {occurrences}.", "occurrence_mismatch");
            }

            string updated = content.Replace(oldText, newText, StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, updated, Utf8NoBom, cancellationToken).ConfigureAwait(false);
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
        public RunCommandTool(string workspaceRoot)
            : base(workspaceRoot, "run_command", "Run a command with captured output by default.", """{"type":"object","properties":{"command":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"workingDirectory":{"type":"string"},"timeoutSeconds":{"type":"integer"},"mode":{"type":"string","enum":["captured","interactive"]}},"required":["command"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            string mode = invocation.Arguments.TryGetProperty("mode", out JsonElement modeElement) && modeElement.ValueKind == JsonValueKind.String
                ? modeElement.GetString() ?? "captured"
                : "captured";

            if (!string.Equals(mode, "captured", StringComparison.OrdinalIgnoreCase))
            {
                return new ToolResult(false, "Interactive command execution is implemented in the Windows platform phase.", "interactive_not_available");
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = RequireString(invocation.Arguments, "command"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = invocation.Arguments.TryGetProperty("workingDirectory", out JsonElement workingDirectory) && workingDirectory.ValueKind == JsonValueKind.String
                    ? ResolveWorkspacePath(invocation.Arguments, "workingDirectory")
                    : WorkspaceRoot
            };

            if (invocation.Arguments.TryGetProperty("arguments", out JsonElement arguments) && arguments.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement argument in arguments.EnumerateArray())
                {
                    startInfo.ArgumentList.Add(argument.GetString() ?? string.Empty);
                }
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start process.");

            int timeoutSeconds = OptionalInt32(invocation.Arguments, "timeoutSeconds", 60);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            string stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string content = string.Concat("exit_code: ", process.ExitCode, Environment.NewLine, "stdout:", Environment.NewLine, stdout, Environment.NewLine, "stderr:", Environment.NewLine, stderr);
            return new ToolResult(process.ExitCode == 0, content, process.ExitCode == 0 ? null : "nonzero_exit");
        }
    }

    private sealed class GlobTool : BuiltinTool
    {
        public GlobTool(string workspaceRoot)
            : base(workspaceRoot, "glob", "List files matching a glob pattern.", """{"type":"object","properties":{"pattern":{"type":"string"},"maxResults":{"type":"integer"}},"required":["pattern"]}""")
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
        public GrepTool(string workspaceRoot)
            : base(workspaceRoot, "grep", "Search text files.", """{"type":"object","properties":{"pattern":{"type":"string"},"filePattern":{"type":"string"},"maxResults":{"type":"integer"}},"required":["pattern"]}""")
        {
        }

        public override async ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Regex regex = new(RequireString(invocation.Arguments, "pattern"), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            string filePattern = invocation.Arguments.TryGetProperty("filePattern", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "*"
                : "*";
            int maxResults = OptionalInt32(invocation.Arguments, "maxResults", 100);

            List<string> results = [];
            foreach (string file in Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(WorkspaceRoot, file).Replace('\\', '/');
                if (!FileSystemName.MatchesSimpleExpression(filePattern, relative, ignoreCase: true))
                {
                    continue;
                }

                if (await IsBinaryFileAsync(file, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string[] lines = await File.ReadAllLinesAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
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
}
