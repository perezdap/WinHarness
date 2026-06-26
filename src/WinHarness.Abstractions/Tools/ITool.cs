using System.Text.Json;

namespace WinHarness.Tools;

/// <summary>
/// Represents a provider-independent tool.
/// </summary>
public interface ITool
{
    /// <summary>
    /// Gets the tool name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the tool description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the JSON schema for tool input.
    /// </summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// Executes the tool.
    /// </summary>
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>
/// Discovers tools from one source.
/// </summary>
public interface IToolProvider
{
    /// <summary>
    /// Lists available tools.
    /// </summary>
    ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Tool invocation payload.
/// </summary>
public sealed record ToolInvocation(string ToolName, JsonElement Arguments);

/// <summary>
/// Tool execution result.
/// </summary>
public sealed record ToolResult(
    bool Succeeded,
    string Content,
    string? ErrorCode = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
