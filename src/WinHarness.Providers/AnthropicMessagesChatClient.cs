using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace WinHarness.Providers;

/// <summary>
/// Hand-rolled Anthropic Messages API client implementing <see cref="IChatClient"/>.
/// Streaming, tool calls, usage, and thinking only — no SDK, AOT-safe (ADR-0005 / PR-B3).
/// </summary>
internal sealed class AnthropicMessagesChatClient : IChatClient
{
    private const int DefaultMaxTokens = 16_384;
    private const int MinThinkingBudgetTokens = 1_024;
    internal const string ThinkingSignatureProperty = "anthropic_thinking_signature";
    private static readonly ChatClientMetadata Metadata = new("anthropic");

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _modelId;
    private readonly IAuthTokenSource _tokenSource;
    private readonly bool _useOAuth;
    private readonly bool _ownsHttp;

    public AnthropicMessagesChatClient(
        HttpClient http,
        Uri endpoint,
        string modelId,
        IAuthTokenSource tokenSource,
        bool useOAuth,
        bool ownsHttp = false)
    {
        _http = http;
        _endpoint = endpoint;
        _modelId = modelId;
        _tokenSource = tokenSource;
        _useOAuth = useOAuth;
        _ownsHttp = ownsHttp;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        byte[] body = BuildRequestBody(messages, options, stream: true);

        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyAuthHeaders(request, token);

        using HttpResponseMessage response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(FormatHttpError(response, errorBody));
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8);

        Dictionary<int, ToolUseAccumulator> toolBlocks = [];
        Dictionary<int, ThinkingAccumulator> thinkingBlocks = [];
        long? inputTokens = null;
        long? outputTokens = null;
        string? messageId = null;
        string? modelId = _modelId;

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            string data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            string? type = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out JsonElement message))
                    {
                        if (message.TryGetProperty("id", out JsonElement idElement))
                        {
                            messageId = idElement.GetString();
                        }

                        if (message.TryGetProperty("model", out JsonElement modelElement))
                        {
                            modelId = modelElement.GetString() ?? modelId;
                        }

                        if (message.TryGetProperty("usage", out JsonElement usage) &&
                            usage.TryGetProperty("input_tokens", out JsonElement input))
                        {
                            inputTokens = input.GetInt64();
                        }
                    }

                    break;

                case "content_block_start":
                    HandleContentBlockStart(root, toolBlocks, thinkingBlocks);
                    break;

                case "content_block_delta":
                    foreach (ChatResponseUpdate update in HandleContentBlockDelta(
                                 root, toolBlocks, thinkingBlocks, messageId, modelId))
                    {
                        yield return update;
                    }

                    break;

                case "content_block_stop":
                    foreach (ChatResponseUpdate update in HandleContentBlockStop(
                                 root, toolBlocks, thinkingBlocks, messageId, modelId))
                    {
                        yield return update;
                    }

                    break;

                case "message_delta":
                    if (root.TryGetProperty("usage", out JsonElement deltaUsage) &&
                        deltaUsage.TryGetProperty("output_tokens", out JsonElement output))
                    {
                        outputTokens = output.GetInt64();
                    }

                    break;

                case "error":
                    string errorMessage = root.TryGetProperty("error", out JsonElement error) &&
                                          error.TryGetProperty("message", out JsonElement errorText)
                        ? errorText.GetString() ?? "Anthropic stream error."
                        : "Anthropic stream error.";
                    throw new InvalidOperationException(errorMessage);

                case "ping":
                case "message_stop":
                    break;
            }
        }

        if (inputTokens is not null || outputTokens is not null)
        {
            UsageDetails details = new()
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = (inputTokens ?? 0) + (outputTokens ?? 0)
            };
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(details)])
            {
                ModelId = modelId,
                MessageId = messageId,
            };
        }
    }

    private void ApplyAuthHeaders(HttpRequestMessage request, string token)
    {
        IReadOnlyDictionary<string, string> headers = _useOAuth
            ? AnthropicOAuthFlow.OAuthRequestHeaders
            : AnthropicOAuthFlow.ApiKeyRequestHeaders;

        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (_useOAuth)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("x-api-key", token);
        }
    }

    private byte[] BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        using MemoryStream streamBuffer = new();
        using (Utf8JsonWriter writer = new(streamBuffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options?.ModelId ?? _modelId);

            int maxTokens = options?.MaxOutputTokens ?? DefaultMaxTokens;
            int? thinkingBudget = null;
            if (options?.Reasoning is { Effort: { } effort } && effort != ReasoningEffort.None)
            {
                thinkingBudget = ResolveThinkingBudget(effort, ref maxTokens);
            }

            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteBoolean("stream", stream);

            List<string> systemParts = [];
            List<ChatMessage> conversation = [];
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.System)
                {
                    if (!string.IsNullOrEmpty(message.Text))
                    {
                        systemParts.Add(message.Text);
                    }

                    continue;
                }

                conversation.Add(message);
            }

            if (systemParts.Count > 0)
            {
                writer.WriteString("system", string.Join("\n\n", systemParts));
            }

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            WriteCoalescedMessages(writer, conversation);
            writer.WriteEndArray();

            if (options?.Tools is { Count: > 0 } tools)
            {
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (AITool tool in tools)
                {
                    if (tool is not AIFunction function)
                    {
                        continue;
                    }

                    writer.WriteStartObject();
                    writer.WriteString("name", function.Name);
                    if (!string.IsNullOrEmpty(function.Description))
                    {
                        writer.WriteString("description", function.Description);
                    }

                    writer.WritePropertyName("input_schema");
                    function.JsonSchema.WriteTo(writer);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (thinkingBudget is int budget)
            {
                writer.WritePropertyName("thinking");
                writer.WriteStartObject();
                writer.WriteString("type", "enabled");
                writer.WriteNumber("budget_tokens", budget);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return streamBuffer.ToArray();
    }

    /// <summary>
    /// Anthropic requires strictly alternating roles. WinHarness emits one
    /// <see cref="ChatRole.Tool"/> message per result and may insert steering
    /// user text immediately after tools — coalesce those into a single user turn.
    /// Consecutive same-role user/assistant messages are merged the same way.
    /// </summary>
    private static void WriteCoalescedMessages(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> conversation)
    {
        int index = 0;
        while (index < conversation.Count)
        {
            ChatMessage message = conversation[index];
            if (message.Role == ChatRole.Tool || message.Role == ChatRole.User)
            {
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WritePropertyName("content");
                writer.WriteStartArray();

                bool wrote = false;
                while (index < conversation.Count &&
                       (conversation[index].Role == ChatRole.Tool || conversation[index].Role == ChatRole.User))
                {
                    wrote |= WriteUserOrToolContents(writer, conversation[index]);
                    index++;
                }

                if (!wrote)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", string.Empty);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                continue;
            }

            // Assistant (and any unexpected role treated as assistant content).
            writer.WriteStartObject();
            writer.WriteString("role", "assistant");
            writer.WritePropertyName("content");
            writer.WriteStartArray();

            bool wroteAssistant = false;
            do
            {
                wroteAssistant |= WriteAssistantContents(writer, conversation[index]);
                index++;
            }
            while (index < conversation.Count && conversation[index].Role == ChatRole.Assistant);

            if (!wroteAssistant)
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }

    private static int ResolveThinkingBudget(ReasoningEffort effort, ref int maxTokens)
    {
        int budget = effort switch
        {
            ReasoningEffort.Low => 4_000,
            ReasoningEffort.Medium => 10_000,
            ReasoningEffort.High => 20_000,
            ReasoningEffort.ExtraHigh => 32_000,
            _ => 10_000
        };

        budget = Math.Max(MinThinkingBudgetTokens, budget);
        // Anthropic requires budget_tokens < max_tokens (without interleaved-thinking beta).
        if (budget >= maxTokens)
        {
            maxTokens = budget + MinThinkingBudgetTokens;
        }

        return budget;
    }

    private static bool WriteUserOrToolContents(Utf8JsonWriter writer, ChatMessage message)
    {
        bool wrote = false;
        if (message.Role == ChatRole.Tool)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionResultContent result)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("type", "tool_result");
                writer.WriteString("tool_use_id", result.CallId);
                writer.WriteString("content", FormatToolResult(result));
                if (result.Exception is not null)
                {
                    writer.WriteBoolean("is_error", true);
                }

                writer.WriteEndObject();
                wrote = true;
            }

            return wrote;
        }

        foreach (AIContent content in message.Contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", text.Text);
                writer.WriteEndObject();
                wrote = true;
            }
        }

        if (!wrote && !string.IsNullOrEmpty(message.Text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", message.Text);
            writer.WriteEndObject();
            wrote = true;
        }

        return wrote;
    }

    private static bool WriteAssistantContents(Utf8JsonWriter writer, ChatMessage message)
    {
        bool wrote = false;
        StringBuilder? thinkingText = null;
        string? thinkingSignature = null;

        foreach (AIContent content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    FlushThinking(writer, ref thinkingText, ref thinkingSignature, ref wrote);
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text.Text);
                    writer.WriteEndObject();
                    wrote = true;
                    break;

                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text) ||
                                                         TryGetThinkingSignature(reasoning, out _):
                    thinkingText ??= new StringBuilder();
                    if (!string.IsNullOrEmpty(reasoning.Text))
                    {
                        thinkingText.Append(reasoning.Text);
                    }

                    if (TryGetThinkingSignature(reasoning, out string? signature) &&
                        !string.IsNullOrEmpty(signature))
                    {
                        thinkingSignature = signature;
                    }

                    break;

                case FunctionCallContent call:
                    FlushThinking(writer, ref thinkingText, ref thinkingSignature, ref wrote);
                    writer.WriteStartObject();
                    writer.WriteString("type", "tool_use");
                    writer.WriteString("id", call.CallId);
                    writer.WriteString("name", call.Name);
                    writer.WritePropertyName("input");
                    WriteArgumentsObject(writer, call.Arguments);
                    writer.WriteEndObject();
                    wrote = true;
                    break;
            }
        }

        FlushThinking(writer, ref thinkingText, ref thinkingSignature, ref wrote);

        if (!wrote && !string.IsNullOrEmpty(message.Text))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", message.Text);
            writer.WriteEndObject();
            wrote = true;
        }

        return wrote;
    }

    private static void FlushThinking(
        Utf8JsonWriter writer,
        ref StringBuilder? thinkingText,
        ref string? thinkingSignature,
        ref bool wrote)
    {
        if (thinkingText is null && string.IsNullOrEmpty(thinkingSignature))
        {
            return;
        }

        string text = thinkingText?.ToString() ?? string.Empty;
        if (text.Length == 0 && string.IsNullOrEmpty(thinkingSignature))
        {
            thinkingText = null;
            thinkingSignature = null;
            return;
        }

        // Anthropic rejects thinking blocks without a signature on continuations.
        // Skip unsigned thinking rather than sending a request that will 400.
        if (string.IsNullOrEmpty(thinkingSignature))
        {
            thinkingText = null;
            thinkingSignature = null;
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", "thinking");
        writer.WriteString("thinking", text);
        writer.WriteString("signature", thinkingSignature);
        writer.WriteEndObject();
        wrote = true;
        thinkingText = null;
        thinkingSignature = null;
    }

    private static bool TryGetThinkingSignature(AIContent content, out string? signature)
    {
        signature = null;
        if (content.AdditionalProperties is null)
        {
            return false;
        }

        if (!content.AdditionalProperties.TryGetValue(ThinkingSignatureProperty, out object? value) ||
            value is null)
        {
            return false;
        }

        signature = value as string ?? value.ToString();
        return !string.IsNullOrEmpty(signature);
    }

    private static void WriteArgumentsObject(Utf8JsonWriter writer, IDictionary<string, object?>? arguments)
    {
        writer.WriteStartObject();
        if (arguments is not null)
        {
            foreach ((string key, object? value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteJsonValue(writer, value);
            }
        }

        writer.WriteEndObject();
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

    private static string FormatToolResult(FunctionResultContent result)
    {
        if (result.Result is null)
        {
            return result.Exception?.Message ?? string.Empty;
        }

        return result.Result switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => result.Result.ToString() ?? string.Empty
        };
    }

    private static void HandleContentBlockStart(
        JsonElement root,
        Dictionary<int, ToolUseAccumulator> toolBlocks,
        Dictionary<int, ThinkingAccumulator> thinkingBlocks)
    {
        if (!root.TryGetProperty("index", out JsonElement indexElement) ||
            !root.TryGetProperty("content_block", out JsonElement block))
        {
            return;
        }

        int index = indexElement.GetInt32();
        string? blockType = block.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString()
            : null;

        if (blockType == "tool_use")
        {
            string id = block.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
            string name = block.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "";
            toolBlocks[index] = new ToolUseAccumulator(id, name);
            return;
        }

        if (blockType == "thinking")
        {
            thinkingBlocks[index] = new ThinkingAccumulator();
        }
    }

    private static IEnumerable<ChatResponseUpdate> HandleContentBlockDelta(
        JsonElement root,
        Dictionary<int, ToolUseAccumulator> toolBlocks,
        Dictionary<int, ThinkingAccumulator> thinkingBlocks,
        string? messageId,
        string? modelId)
    {
        if (!root.TryGetProperty("index", out JsonElement indexElement) ||
            !root.TryGetProperty("delta", out JsonElement delta))
        {
            yield break;
        }

        int index = indexElement.GetInt32();
        string? deltaType = delta.TryGetProperty("type", out JsonElement typeElement)
            ? typeElement.GetString()
            : null;

        switch (deltaType)
        {
            case "text_delta":
                if (delta.TryGetProperty("text", out JsonElement textElement))
                {
                    string text = textElement.GetString() ?? string.Empty;
                    if (text.Length > 0)
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                        {
                            ModelId = modelId,
                            MessageId = messageId,
                        };
                    }
                }

                break;

            case "thinking_delta":
                if (thinkingBlocks.TryGetValue(index, out ThinkingAccumulator? thinking) &&
                    delta.TryGetProperty("thinking", out JsonElement thinkingElement))
                {
                    thinking.Text.Append(thinkingElement.GetString());
                }

                break;

            case "input_json_delta":
                if (toolBlocks.TryGetValue(index, out ToolUseAccumulator? tool) &&
                    delta.TryGetProperty("partial_json", out JsonElement partial))
                {
                    tool.Json.Append(partial.GetString());
                }

                break;

            case "signature_delta":
                if (thinkingBlocks.TryGetValue(index, out ThinkingAccumulator? signed) &&
                    delta.TryGetProperty("signature", out JsonElement signatureElement))
                {
                    signed.Signature.Append(signatureElement.GetString());
                }

                break;
        }
    }

    private static IEnumerable<ChatResponseUpdate> HandleContentBlockStop(
        JsonElement root,
        Dictionary<int, ToolUseAccumulator> toolBlocks,
        Dictionary<int, ThinkingAccumulator> thinkingBlocks,
        string? messageId,
        string? modelId)
    {
        if (!root.TryGetProperty("index", out JsonElement indexElement))
        {
            yield break;
        }

        int index = indexElement.GetInt32();
        if (thinkingBlocks.Remove(index, out ThinkingAccumulator? thinking))
        {
            string text = thinking.Text.ToString();
            string signature = thinking.Signature.ToString();
            if (text.Length > 0 || signature.Length > 0)
            {
                TextReasoningContent reasoning = new(text);
                if (signature.Length > 0)
                {
                    reasoning.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [ThinkingSignatureProperty] = signature
                    };
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, [reasoning])
                {
                    ModelId = modelId,
                    MessageId = messageId,
                };
            }
        }

        if (!toolBlocks.Remove(index, out ToolUseAccumulator? tool))
        {
            yield break;
        }

        IDictionary<string, object?>? arguments = ParseArguments(tool.Json.ToString());
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent(tool.Id, tool.Name, arguments)])
        {
            ModelId = modelId,
            MessageId = messageId,
        };
    }

    private static IDictionary<string, object?>? ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = document.RootElement.GetRawText()
                };
            }

            Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = ConvertJsonElement(property.Value);
            }

            return arguments;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["raw"] = json
            };
        }
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long value) => value,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };

    private static string FormatHttpError(HttpResponseMessage response, string body)
    {
        int status = (int)response.StatusCode;
        string? anthropicMessage = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message))
            {
                anthropicMessage = message.GetString();
            }
        }
        catch (JsonException)
        {
        }

        string detail = anthropicMessage ?? (string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "error" : body);
        return status switch
        {
            401 => $"Anthropic authentication failed (401). Re-run 'winharness login --provider anthropic' or check the API key. {detail}",
            403 => $"Anthropic rejected the request (403). Subscription may be expired or the grant revoked. {detail}",
            429 => $"Anthropic rate limit (429). Wait and retry. {detail}",
            529 => $"Anthropic overloaded (529). Retry shortly. {detail}",
            _ => $"Anthropic request failed ({status}): {detail}"
        };
    }

    private sealed class ToolUseAccumulator(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Json { get; } = new();
    }

    private sealed class ThinkingAccumulator
    {
        public StringBuilder Text { get; } = new();
        public StringBuilder Signature { get; } = new();
    }
}
