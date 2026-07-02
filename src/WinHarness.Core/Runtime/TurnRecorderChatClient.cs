using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using WinHarness.Conversation;

namespace WinHarness.Runtime;

/// <summary>
/// Wraps a raw <see cref="IChatClient"/> and records all messages produced during a tool invocation loop.
/// </summary>
internal sealed class TurnRecorderChatClient : DelegatingChatClient
{
    private readonly Dictionary<string, string> _toolCallNames = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _turnMessages = [];
    private List<ChatMessage> _lastInputSnapshot = [];
    private SteeringQueue? _steering;

    private TurnRecorderChatClient(IChatClient inner)
        : base(inner)
    {
    }

    /// <summary>
    /// Wraps a raw provider client with turn recording inside function invocation middleware.
    /// </summary>
    public static (TurnRecorderChatClient Recorder, IChatClient Client) Create(
        IChatClient innerClient,
        SteeringQueue? steering = null)
    {
        TurnRecorderChatClient recorder = new(innerClient) { _steering = steering };
        IChatClient client = new ChatClientBuilder(recorder)
            .UseFunctionInvocation()
            .Build();

        return (recorder, client);
    }

    /// <summary>
    /// Builds turn artifacts for the completed request.
    /// </summary>
    public TurnArtifacts BuildTurnArtifacts(
        ConversationMessage userMessage,
        string providerId,
        string modelId,
        MessageUsage? usage)
    {
        List<ConversationMessage> messages = [userMessage];

        foreach (ChatMessage chatMessage in _turnMessages)
        {
            foreach (ConversationMessage converted in ConvertCapturedMessages(chatMessage))
            {
                ConversationMessage message = converted.Role == ConversationRole.Assistant
                    ? converted with { ProviderId = providerId, ModelId = modelId }
                    : converted;
                messages.Add(message);
            }
        }

        if (usage is not null)
        {
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                if (messages[index].Role != ConversationRole.Assistant)
                {
                    continue;
                }

                messages[index] = messages[index] with { Usage = usage };
                break;
            }
        }

        return new TurnArtifacts(messages);
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> currentInput = messages.ToList();
        InjectSteeringMessages(currentInput);
        BeginInnerCall(currentInput);

        ChatResponse response = await base.GetResponseAsync(currentInput, options, cancellationToken).ConfigureAwait(false);
        CaptureResponse(response.Messages);
        EndInnerCall(currentInput);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ChatMessage> currentInput = messages.ToList();
        InjectSteeringMessages(currentInput);
        BeginInnerCall(currentInput);
        List<ChatResponseUpdate> updates = [];

        await foreach (ChatResponseUpdate update in base
                           .GetStreamingResponseAsync(currentInput, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            updates.Add(update);
            yield return update;
        }

        ChatResponse response = updates.ToChatResponse();
        CaptureResponse(response.Messages);
        EndInnerCall(currentInput);
    }

    /// <summary>
    /// Drains pending steering messages into the round-trip input as user
    /// messages. Only applies when the input ends with tool results — i.e. the
    /// model is being re-invoked after executing tool calls — so steering is
    /// delivered after the current assistant segment finishes its tools, never
    /// spliced into the first request of a turn.
    /// </summary>
    private void InjectSteeringMessages(List<ChatMessage> currentInput)
    {
        if (_steering is null || _steering.Count == 0 || currentInput.Count == 0)
        {
            return;
        }

        if (currentInput[^1].Role != ChatRole.Tool)
        {
            return;
        }

        foreach (string text in _steering.DrainAll())
        {
            ChatMessage message = new(ChatRole.User, text);
            currentInput.Add(message);
            _turnMessages.Add(message);
        }
    }

    private void BeginInnerCall(IReadOnlyList<ChatMessage> currentInput)
    {
        if (IsNewTurn(currentInput))
        {
            ResetCapture();
        }

        CaptureToolResultsFromInput(currentInput);
    }

    private void EndInnerCall(IReadOnlyList<ChatMessage> currentInput)
    {
        _lastInputSnapshot = currentInput.ToList();
    }

    private void ResetCapture()
    {
        _toolCallNames.Clear();
        _turnMessages.Clear();
        _lastInputSnapshot = [];
    }

    private bool IsNewTurn(IReadOnlyList<ChatMessage> currentInput)
    {
        if (_lastInputSnapshot.Count == 0)
        {
            return _turnMessages.Count == 0;
        }

        if (currentInput.Count <= _lastInputSnapshot.Count)
        {
            return true;
        }

        for (int index = 0; index < _lastInputSnapshot.Count; index++)
        {
            if (!ReferenceEquals(currentInput[index], _lastInputSnapshot[index]) &&
                !ChatMessageEquals(currentInput[index], _lastInputSnapshot[index]))
            {
                return true;
            }
        }

        return false;
    }

    private void CaptureToolResultsFromInput(IReadOnlyList<ChatMessage> currentInput)
    {
        if (_lastInputSnapshot.Count == 0)
        {
            return;
        }

        for (int index = _lastInputSnapshot.Count; index < currentInput.Count; index++)
        {
            ChatMessage message = currentInput[index];
            if (message.Role == ChatRole.Tool)
            {
                _turnMessages.Add(message);
            }
        }
    }

    private void CaptureResponse(IList<ChatMessage>? responseMessages)
    {
        if (responseMessages is null)
        {
            return;
        }

        foreach (ChatMessage message in responseMessages)
        {
            if (message.Role == ChatRole.Assistant)
            {
                _turnMessages.Add(message);
                IndexToolCalls(message);
            }
        }
    }

    private void IndexToolCalls(ChatMessage message)
    {
        foreach (AIContent content in message.Contents)
        {
            if (content is FunctionCallContent functionCall)
            {
                _toolCallNames[functionCall.CallId] = functionCall.Name;
            }
        }
    }

    private static bool ChatMessageEquals(ChatMessage left, ChatMessage right)
    {
        return left.Role == right.Role &&
               string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
               left.Contents.Count == right.Contents.Count;
    }

    private IEnumerable<ConversationMessage> ConvertCapturedMessages(ChatMessage message)
    {
        if (message.Role == ChatRole.User)
        {
            // Steering messages injected mid-turn.
            if (message.Text.Length > 0)
            {
                yield return ConversationMessage.FromText(ConversationRole.User, message.Text);
            }

            yield break;
        }

        if (message.Role == ChatRole.Tool)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionResultContent functionResult)
                {
                    continue;
                }

                string toolName = _toolCallNames.GetValueOrDefault(functionResult.CallId, "unknown");
                yield return new ConversationMessage(
                    ConversationRole.Tool,
                    [
                        ContentBlock.CreateToolResult(
                            functionResult.CallId,
                            toolName,
                            FormatFunctionResult(functionResult),
                            functionResult.Exception is not null)
                    ]);
            }

            yield break;
        }

        if (message.Role != ChatRole.Assistant)
        {
            yield break;
        }

        List<ContentBlock> blocks = [];
        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    blocks.Add(ContentBlock.CreateText(textContent.Text));
                    break;
                case FunctionCallContent functionCall:
                    _toolCallNames[functionCall.CallId] = functionCall.Name;
                    blocks.Add(ContentBlock.CreateToolCall(
                        functionCall.CallId,
                        functionCall.Name,
                        SerializeToolArguments(functionCall.Arguments)));
                    break;
            }
        }

        if (blocks.Count == 0)
        {
            yield break;
        }

        yield return new ConversationMessage(ConversationRole.Assistant, blocks);
    }

    private static string FormatFunctionResult(FunctionResultContent functionResult)
    {
        if (functionResult.Result is null)
        {
            return functionResult.Exception?.Message ?? string.Empty;
        }

        return functionResult.Result switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => functionResult.Result.ToString() ?? string.Empty
        };
    }

    private static string SerializeToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> argument in arguments)
        {
            writer.WritePropertyName(argument.Key);
            WriteJsonValue(writer, argument.Value);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}