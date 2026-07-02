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
        _sanitizedName = ToolNameSanitizer.Sanitize(tool.Name);
        _sanitizedSchema = ToolSchemaSanitizer.Sanitize(tool.InputSchema);
    }

    private readonly string _sanitizedName;
    private readonly JsonElement _sanitizedSchema;

    /// <inheritdoc />
    public override string Name => _sanitizedName;

    /// <inheritdoc />
    public override string Description => _tool.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => _sanitizedSchema;

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
                    MergeMetadata(
                        new Dictionary<string, string>
                        {
                            ["tool.name"] = _tool.Name,
                            ["tool.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                            ["tool.succeeded"] = result.Succeeded.ToString(CultureInfo.InvariantCulture),
                            ["tool.error_code"] = result.ErrorCode ?? string.Empty
                        },
                        result.Metadata)),
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

    private static Dictionary<string, string> MergeMetadata(
        Dictionary<string, string> properties,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return properties;
        }

        foreach (KeyValuePair<string, string> pair in metadata)
        {
            properties[pair.Key] = pair.Value;
        }

        return properties;
    }

    private static class ToolSchemaSanitizer
    {
        public static JsonElement Sanitize(JsonElement schema)
        {
            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                WriteSchemaNode(writer, schema);
            }

            using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }

        // Writes a single JSON-Schema node (an object describing a type, or a
        // composition/array of such). Normalizes the node so Gemini's
        // OpenAI-compatible tool bridge accepts it.
        private static void WriteSchemaNode(Utf8JsonWriter writer, JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                node.WriteTo(writer);
                return;
            }

            bool hasType = false;
            bool hasProperties = false;
            bool hasItems = false;
            bool hasRef = false;
            bool hasCompositionKeyword = false;

            foreach (JsonProperty property in node.EnumerateObject())
            {
                hasType |= property.NameEquals("type");
                hasProperties |= property.NameEquals("properties");
                hasItems |= property.NameEquals("items");
                hasRef |= property.NameEquals("$ref");
                hasCompositionKeyword |= property.NameEquals("anyOf") || property.NameEquals("oneOf") || property.NameEquals("allOf");
            }

            writer.WriteStartObject();
            foreach (JsonProperty property in node.EnumerateObject())
            {
                // Gemini rejects non-string enums (e.g. boolean-only `enum: [true]`),
                // sometimes by returning an empty 200 stream. Drop those entirely.
                if (property.NameEquals("enum") && !IsStringEnum(property.Value))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);

                if (property.NameEquals("properties"))
                {
                    WritePropertiesMap(writer, property.Value);
                }
                else if (property.NameEquals("items"))
                {
                    WriteSchemaNode(writer, property.Value);
                }
                else if (property.NameEquals("anyOf") || property.NameEquals("oneOf") || property.NameEquals("allOf"))
                {
                    WriteSchemaArray(writer, property.Value);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            // Some MCP tools expose untyped property definitions (a `value` field with
            // only a description). Gemini requires a concrete type, so default to
            // string when nothing else constrains the node.
            if (!hasType && !hasProperties && !hasItems && !hasRef && !hasCompositionKeyword)
            {
                writer.WriteString("type", "string");
            }

            writer.WriteEndObject();
        }

        // Writes a JSON-Schema `properties` map: each value is itself a schema node.
        private static void WritePropertiesMap(Utf8JsonWriter writer, JsonElement propertiesMap)
        {
            if (propertiesMap.ValueKind != JsonValueKind.Object)
            {
                propertiesMap.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (JsonProperty property in propertiesMap.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                WriteSchemaNode(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        private static void WriteSchemaArray(Utf8JsonWriter writer, JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                array.WriteTo(writer);
                return;
            }

            writer.WriteStartArray();
            foreach (JsonElement item in array.EnumerateArray())
            {
                WriteSchemaNode(writer, item);
            }

            writer.WriteEndArray();
        }

        private static bool IsStringEnum(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
            }

            return true;
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
