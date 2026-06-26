using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace WinHarness.Tools;

/// <summary>
/// Adapts a WinHarness tool to an explicit <see cref="AIFunction"/> without reflection.
/// </summary>
public sealed class ToolAIFunctionAdapter : AIFunction
{
    private readonly ITool _tool;

    /// <summary>
    /// Creates a tool function adapter.
    /// </summary>
    public ToolAIFunctionAdapter(ITool tool)
    {
        _tool = tool;
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
        ToolResult result = await _tool.ExecuteAsync(
            new ToolInvocation(_tool.Name, jsonArguments),
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.Content : $"Tool failed ({result.ErrorCode}): {result.Content}";
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
}
