using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WinHarness.Diagnostics;

namespace WinHarness.Tools;

/// <summary>
/// Adapts a WinHarness tool to an explicit <see cref="AIFunction"/> without reflection.
/// </summary>
public sealed class ToolAIFunctionAdapter : AIFunction
{
    private static readonly IDiagnosticSink NoDiagnostics = new NullDiagnosticSink();
    private static readonly IToolActivitySink NoActivity = new NullToolActivitySink();

    private readonly IToolActivitySink _activitySink;
    private readonly IDiagnosticSink _diagnosticSink;
    private readonly ITool _tool;

    /// <summary>
    /// Creates a tool function adapter.
    /// </summary>
    public ToolAIFunctionAdapter(ITool tool)
        : this(tool, NoDiagnostics, NoActivity)
    {
    }

    /// <summary>
    /// Creates a tool function adapter.
    /// </summary>
    public ToolAIFunctionAdapter(ITool tool, IDiagnosticSink diagnosticSink)
        : this(tool, diagnosticSink, NoActivity)
    {
    }

    /// <summary>
    /// Creates a tool function adapter.
    /// </summary>
    public ToolAIFunctionAdapter(
        ITool tool,
        IDiagnosticSink diagnosticSink,
        IToolActivitySink activitySink)
    {
        _tool = tool;
        _diagnosticSink = diagnosticSink;
        _activitySink = activitySink;
    }

    /// <inheritdoc />
    public override string Name => _tool.Name;

    /// <inheritdoc />
    public override string Description => _tool.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _tool.InputSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        JsonElement jsonArguments = ConvertArguments(arguments);
        Stopwatch stopwatch = Stopwatch.StartNew();
        _activitySink.ToolStarted(_tool.Name);

        try
        {
            ToolResult result = await _tool.ExecuteAsync(
                new ToolInvocation(_tool.Name, jsonArguments),
                cancellationToken).ConfigureAwait(false);

            await _diagnosticSink.WriteAsync(
                new DiagnosticRecord(
                    DateTimeOffset.UtcNow,
                    "tool",
                    result.Succeeded ? "tool.completed" : "tool.failed",
                    _tool.Name,
                    new Dictionary<string, string>
                    {
                        ["tool.name"] = _tool.Name,
                        ["tool.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                        ["tool.succeeded"] = result.Succeeded.ToString(CultureInfo.InvariantCulture),
                        ["tool.error_code"] = result.ErrorCode ?? string.Empty
                    }),
                cancellationToken).ConfigureAwait(false);

            _activitySink.ToolCompleted(_tool.Name, result, stopwatch.Elapsed);

            return result.Succeeded ? result.Content : $"Tool failed ({result.ErrorCode}): {result.Content}";
        }
        catch (Exception ex)
        {
            _activitySink.ToolFailed(_tool.Name, ex, stopwatch.Elapsed);

            await _diagnosticSink.WriteAsync(
                new DiagnosticRecord(
                    DateTimeOffset.UtcNow,
                    "tool",
                    "tool.exception",
                    _tool.Name,
                    new Dictionary<string, string>
                    {
                        ["tool.name"] = _tool.Name,
                        ["tool.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                        ["exception.type"] = ex.GetType().FullName ?? ex.GetType().Name
                    }),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static JsonElement ConvertArguments(AIFunctionArguments arguments)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object?> argument in arguments)
            {
                writer.WritePropertyName(argument.Key);
                WriteValue(writer, argument.Value);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string stringValue:
                writer.WriteStringValue(stringValue);
                break;
            case bool boolValue:
                writer.WriteBooleanValue(boolValue);
                break;
            case int intValue:
                writer.WriteNumberValue(intValue);
                break;
            case long longValue:
                writer.WriteNumberValue(longValue);
                break;
            case double doubleValue:
                writer.WriteNumberValue(doubleValue);
                break;
            case float floatValue:
                writer.WriteNumberValue(floatValue);
                break;
            case decimal decimalValue:
                writer.WriteNumberValue(decimalValue);
                break;
            case JsonElement json:
                json.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private sealed class NullDiagnosticSink : IDiagnosticSink
    {
        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullToolActivitySink : IToolActivitySink
    {
        public void ToolStarted(string toolName)
        {
        }

        public void ToolCompleted(string toolName, ToolResult result, TimeSpan duration)
        {
        }

        public void ToolFailed(string toolName, Exception exception, TimeSpan duration)
        {
        }
    }
}
