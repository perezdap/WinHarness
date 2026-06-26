using System.Text.Json;

namespace WinHarness.Tools;

/// <summary>
/// Provides built-in tools.
/// </summary>
public sealed class BuiltinToolProvider : IToolProvider
{
    private static readonly IReadOnlyList<ITool> Tools =
    [
        new MetadataTool("read_file", "Read a UTF-8 text file."),
        new MetadataTool("write_file", "Write a UTF-8 text file."),
        new MetadataTool("edit_file", "Replace exact text in a file."),
        new MetadataTool("run_command", "Run a command with captured output by default."),
        new MetadataTool("glob", "List files matching a glob pattern."),
        new MetadataTool("grep", "Search text files.")
    ];

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Tools);
    }

    private sealed class MetadataTool : ITool
    {
        private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone();

        public MetadataTool(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }

        public string Description { get; }

        public JsonElement InputSchema => EmptySchema;

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ToolResult(false, string.Empty, "not_implemented"));
        }
    }
}
