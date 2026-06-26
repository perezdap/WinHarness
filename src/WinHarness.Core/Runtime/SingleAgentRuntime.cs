using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Providers;
using WinHarness.Tools;

namespace WinHarness.Runtime;

/// <summary>
/// Initial single-agent runtime implementation.
/// </summary>
public sealed class SingleAgentRuntime : IAgentRuntime
{
    private readonly IProviderFactory _providerFactory;
    private readonly IEnumerable<IToolProvider> _toolProviders;
    private readonly ILogger<SingleAgentRuntime> _logger;

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(IProviderFactory providerFactory, ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, [], logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        ILogger<SingleAgentRuntime> logger)
    {
        _providerFactory = providerFactory;
        _toolProviders = toolProviders;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IChatProvider provider = _providerFactory.Create(request.ProviderId, request.ModelId);
        using IChatClient client = provider.CreateChatClient();

        _logger.ProviderRequestStarting(provider.ProviderId, provider.ModelId);

        ChatOptions? options = await CreateChatOptionsAsync(cancellationToken).ConfigureAwait(false);

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           request.Prompt,
                           options,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            string text = update.ToString();
            if (text.Length > 0)
            {
                yield return new AgentEvent(AgentEventKind.AssistantDelta, text);
            }
        }

        _logger.ProviderRequestCompleted(provider.ProviderId, provider.ModelId);
        yield return new AgentEvent(AgentEventKind.Completed, "completed");
    }

    private async ValueTask<ChatOptions?> CreateChatOptionsAsync(CancellationToken cancellationToken)
    {
        List<AITool> tools = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (IToolProvider provider in _toolProviders)
        {
            IReadOnlyList<ITool> providerTools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (ITool tool in providerTools)
            {
                if (!names.Add(tool.Name))
                {
                    throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'.");
                }

                tools.Add(new RuntimeToolFunction(tool));
            }
        }

        return tools.Count == 0 ? null : new ChatOptions { Tools = tools };
    }

    private sealed class RuntimeToolFunction : AIFunction
    {
        private readonly ITool _tool;

        public RuntimeToolFunction(ITool tool)
        {
            _tool = tool;
        }

        public override string Name => _tool.Name;

        public override string Description => _tool.Description;

        public override JsonElement JsonSchema => _tool.InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            JsonElement jsonArguments = ConvertArguments(arguments);
            ToolResult result = await _tool.ExecuteAsync(new ToolInvocation(_tool.Name, jsonArguments), cancellationToken)
                .ConfigureAwait(false);

            return result.Succeeded ? result.Content : $"Tool failed ({result.ErrorCode}): {result.Content}";
        }

        private static JsonElement ConvertArguments(AIFunctionArguments arguments)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                foreach (KeyValuePair<string, object?> argument in arguments)
                {
                    writer.WritePropertyName(argument.Key);
                    WriteValue(writer, argument.Value);
                }

                writer.WriteEndObject();
            }

            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
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
                case JsonElement json:
                    json.WriteTo(writer);
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }
    }
}
