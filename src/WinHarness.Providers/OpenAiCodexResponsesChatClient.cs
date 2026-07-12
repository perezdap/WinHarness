using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace WinHarness.Providers;

/// <summary>
/// Hand-rolled OpenAI Codex Responses API client implementing <see cref="IChatClient"/>.
/// Streaming, tool calls, usage, and reasoning only — no SDK, AOT-safe (ADR-0005 / PR-B4).
/// </summary>
internal sealed class OpenAiCodexResponsesChatClient : IChatClient
{
    private static readonly ChatClientMetadata Metadata = new("openai-codex");

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _modelId;
    private readonly IAuthTokenSource _tokenSource;
    private readonly string? _accountId;
    private readonly bool _ownsHttp;

    public OpenAiCodexResponsesChatClient(
        HttpClient http,
        Uri endpoint,
        string modelId,
        IAuthTokenSource tokenSource,
        string? accountId,
        bool ownsHttp = false)
    {
        _http = http;
        _endpoint = endpoint;
        _modelId = modelId;
        _tokenSource = tokenSource;
        _accountId = accountId;
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
        string? accountId = _accountId;
        if (string.IsNullOrEmpty(accountId) && _tokenSource is OAuthTokenSource oauthSource)
        {
            OAuthTokenSet set = await oauthSource.LoadTokenSetAsync(cancellationToken).ConfigureAwait(false);
            accountId = set.AccountId;
        }

        if (string.IsNullOrEmpty(accountId))
        {
            throw new InvalidOperationException(
                "OpenAI Codex requires a chatgpt_account_id. Run 'winharness login --provider openai'.");
        }

        byte[] body = BuildRequestBody(messages, options, stream: true);

        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        ApplyAuthHeaders(request, token, accountId);

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

        Dictionary<int, FunctionCallAccumulator> toolItems = [];
        long? inputTokens = null;
        long? outputTokens = null;
        string? responseId = null;
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
                case "response.created":
                case "response.in_progress":
                    if (root.TryGetProperty("response", out JsonElement created) &&
                        created.TryGetProperty("id", out JsonElement createdId))
                    {
                        responseId = createdId.GetString() ?? responseId;
                    }

                    break;

                case "response.output_text.delta":
                {
                    string? delta = root.TryGetProperty("delta", out JsonElement deltaElement)
                        ? deltaElement.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(delta))
                    {
                        yield return new ChatResponseUpdate(ChatRole.Assistant, delta)
                        {
                            ModelId = modelId,
                            ResponseId = responseId,
                        };
                    }

                    break;
                }

                case "response.output_item.added":
                    HandleOutputItemAdded(root, toolItems);
                    break;

                case "response.function_call_arguments.delta":
                    HandleFunctionCallArgumentsDelta(root, toolItems);
                    break;

                case "response.function_call_arguments.done":
                    HandleFunctionCallArgumentsDone(root, toolItems);
                    break;

                case "response.output_item.done":
                    foreach (ChatResponseUpdate update in HandleOutputItemDone(root, toolItems, responseId, modelId))
                    {
                        yield return update;
                    }

                    break;

                case "response.completed":
                case "response.incomplete":
                    if (root.TryGetProperty("response", out JsonElement completed))
                    {
                        if (completed.TryGetProperty("id", out JsonElement completedId))
                        {
                            responseId = completedId.GetString() ?? responseId;
                        }

                        if (completed.TryGetProperty("model", out JsonElement modelElement))
                        {
                            modelId = modelElement.GetString() ?? modelId;
                        }

                        if (completed.TryGetProperty("usage", out JsonElement usage))
                        {
                            if (usage.TryGetProperty("input_tokens", out JsonElement input))
                            {
                                inputTokens = input.GetInt64();
                            }

                            if (usage.TryGetProperty("output_tokens", out JsonElement output))
                            {
                                outputTokens = output.GetInt64();
                            }
                        }
                    }

                    break;

                case "response.failed":
                case "error":
                {
                    string errorMessage = "OpenAI Codex stream error.";
                    if (root.TryGetProperty("response", out JsonElement failed) &&
                        failed.TryGetProperty("error", out JsonElement error) &&
                        error.TryGetProperty("message", out JsonElement message))
                    {
                        errorMessage = message.GetString() ?? errorMessage;
                    }
                    else if (root.TryGetProperty("error", out JsonElement topError) &&
                             topError.TryGetProperty("message", out JsonElement topMessage))
                    {
                        errorMessage = topMessage.GetString() ?? errorMessage;
                    }

                    throw new InvalidOperationException(errorMessage);
                }
            }
        }

        // Emit any tool calls that never got an output_item.done event.
        foreach ((int _, FunctionCallAccumulator accumulator) in toolItems)
        {
            if (accumulator.Emitted || string.IsNullOrEmpty(accumulator.Name))
            {
                continue;
            }

            yield return CreateFunctionCallUpdate(accumulator, responseId, modelId);
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
                ResponseId = responseId,
            };
        }
    }

    private void ApplyAuthHeaders(HttpRequestMessage request, string token, string accountId)
    {
        foreach ((string name, string value) in OpenAiCodexOAuthFlow.RequestHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            $"winharness ({Environment.OSVersion.Platform}; {RuntimeInformation.OSArchitecture})");
    }

    private byte[] BuildRequestBody(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        using MemoryStream streamBuffer = new();
        using (Utf8JsonWriter writer = new(streamBuffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options?.ModelId ?? _modelId);
            writer.WriteBoolean("store", false);
            writer.WriteBoolean("stream", stream);
            writer.WriteBoolean("parallel_tool_calls", true);
            writer.WriteString("tool_choice", "auto");

            writer.WritePropertyName("text");
            writer.WriteStartObject();
            writer.WriteString("verbosity", "low");
            writer.WriteEndObject();

            // Do not request reasoning.encrypted_content until we round-trip
            // those items on subsequent turns (requesting without replay breaks
            // multi-turn Codex reasoning sessions).

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

            string instructions = systemParts.Count > 0
                ? string.Join("\n\n", systemParts)
                : "You are a helpful assistant.";
            writer.WriteString("instructions", instructions);

            writer.WritePropertyName("input");
            writer.WriteStartArray();
            int messageIndex = 0;
            foreach (ChatMessage message in conversation)
            {
                WriteInputItems(writer, message, messageIndex++);
            }

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
                    writer.WriteString("type", "function");
                    writer.WriteString("name", function.Name);
                    if (!string.IsNullOrEmpty(function.Description))
                    {
                        writer.WriteString("description", function.Description);
                    }

                    writer.WritePropertyName("parameters");
                    function.JsonSchema.WriteTo(writer);
                    writer.WriteBoolean("strict", false);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (options?.Temperature is { } temperature)
            {
                writer.WriteNumber("temperature", temperature);
            }

            if (options?.Reasoning is { Effort: { } effort } && effort != ReasoningEffort.None)
            {
                string effortName = effort switch
                {
                    ReasoningEffort.Low => "low",
                    ReasoningEffort.Medium => "medium",
                    ReasoningEffort.High => "high",
                    ReasoningEffort.ExtraHigh => "xhigh",
                    _ => "medium"
                };

                writer.WritePropertyName("reasoning");
                writer.WriteStartObject();
                writer.WriteString("effort", effortName);
                writer.WriteString("summary", "auto");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return streamBuffer.ToArray();
    }

    private static void WriteInputItems(Utf8JsonWriter writer, ChatMessage message, int messageIndex)
    {
        if (message.Role == ChatRole.User)
        {
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "input_text");
            writer.WriteString("text", message.Text ?? string.Empty);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        if (message.Role == ChatRole.Assistant)
        {
            bool wroteText = false;
            foreach (AIContent content in message.Contents)
            {
                if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "message");
                    writer.WriteString("role", "assistant");
                    writer.WriteString("status", "completed");
                    writer.WriteString("id", $"msg_wh_{messageIndex}");
                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "output_text");
                    writer.WriteString("text", text.Text);
                    writer.WritePropertyName("annotations");
                    writer.WriteStartArray();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    wroteText = true;
                }
                else if (content is FunctionCallContent call)
                {
                    (string callId, string? itemId) = SplitFunctionCallId(call.CallId);
                    writer.WriteStartObject();
                    writer.WriteString("type", "function_call");
                    if (!string.IsNullOrEmpty(itemId))
                    {
                        writer.WriteString("id", itemId);
                    }

                    writer.WriteString("call_id", callId);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", SerializeArguments(call.Arguments));
                    writer.WriteEndObject();
                }
            }

            if (!wroteText && message.Contents.Count == 0 && !string.IsNullOrEmpty(message.Text))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "message");
                writer.WriteString("role", "assistant");
                writer.WriteString("status", "completed");
                writer.WriteString("id", $"msg_wh_{messageIndex}");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "output_text");
                writer.WriteString("text", message.Text);
                writer.WritePropertyName("annotations");
                writer.WriteStartArray();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return;
        }

        if (message.Role == ChatRole.Tool)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not FunctionResultContent result)
                {
                    continue;
                }

                (string callId, _) = SplitFunctionCallId(result.CallId);
                writer.WriteStartObject();
                writer.WriteString("type", "function_call_output");
                writer.WriteString("call_id", callId);
                writer.WriteString("output", FormatToolResult(result));
                writer.WriteEndObject();
            }
        }
    }

    private static void HandleOutputItemAdded(JsonElement root, Dictionary<int, FunctionCallAccumulator> toolItems)
    {
        if (!root.TryGetProperty("output_index", out JsonElement indexElement) ||
            !root.TryGetProperty("item", out JsonElement item))
        {
            return;
        }

        int index = indexElement.GetInt32();
        string? itemType = item.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
        if (itemType != "function_call")
        {
            return;
        }

        string callId = item.TryGetProperty("call_id", out JsonElement callIdElement)
            ? callIdElement.GetString() ?? $"call_{index}"
            : $"call_{index}";
        string? itemId = item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
        string name = item.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        string args = item.TryGetProperty("arguments", out JsonElement argsElement)
            ? argsElement.GetString() ?? string.Empty
            : string.Empty;

        toolItems[index] = new FunctionCallAccumulator(callId, itemId, name, args);
    }

    private static void HandleFunctionCallArgumentsDelta(
        JsonElement root,
        Dictionary<int, FunctionCallAccumulator> toolItems)
    {
        if (!root.TryGetProperty("output_index", out JsonElement indexElement))
        {
            return;
        }

        int index = indexElement.GetInt32();
        if (!toolItems.TryGetValue(index, out FunctionCallAccumulator? accumulator))
        {
            return;
        }

        string? delta = root.TryGetProperty("delta", out JsonElement deltaElement)
            ? deltaElement.GetString()
            : null;
        if (!string.IsNullOrEmpty(delta))
        {
            accumulator.ArgumentsJson.Append(delta);
        }
    }

    private static void HandleFunctionCallArgumentsDone(
        JsonElement root,
        Dictionary<int, FunctionCallAccumulator> toolItems)
    {
        if (!root.TryGetProperty("output_index", out JsonElement indexElement))
        {
            return;
        }

        int index = indexElement.GetInt32();
        if (!toolItems.TryGetValue(index, out FunctionCallAccumulator? accumulator))
        {
            return;
        }

        if (root.TryGetProperty("arguments", out JsonElement argsElement) &&
            argsElement.GetString() is { Length: > 0 } full)
        {
            accumulator.ArgumentsJson.Clear();
            accumulator.ArgumentsJson.Append(full);
        }
    }

    private static IEnumerable<ChatResponseUpdate> HandleOutputItemDone(
        JsonElement root,
        Dictionary<int, FunctionCallAccumulator> toolItems,
        string? responseId,
        string? modelId)
    {
        if (!root.TryGetProperty("output_index", out JsonElement indexElement) ||
            !root.TryGetProperty("item", out JsonElement item))
        {
            yield break;
        }

        int index = indexElement.GetInt32();
        string? itemType = item.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() : null;
        if (itemType != "function_call")
        {
            yield break;
        }

        if (!toolItems.TryGetValue(index, out FunctionCallAccumulator? accumulator))
        {
            string callId = item.TryGetProperty("call_id", out JsonElement callIdElement)
                ? callIdElement.GetString() ?? $"call_{index}"
                : $"call_{index}";
            string? itemId = item.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
            string name = item.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            string args = item.TryGetProperty("arguments", out JsonElement argsElement)
                ? argsElement.GetString() ?? string.Empty
                : string.Empty;
            accumulator = new FunctionCallAccumulator(callId, itemId, name, args);
            toolItems[index] = accumulator;
        }
        else
        {
            if (item.TryGetProperty("arguments", out JsonElement argsElement) &&
                argsElement.GetString() is { Length: > 0 } full)
            {
                accumulator.ArgumentsJson.Clear();
                accumulator.ArgumentsJson.Append(full);
            }

            if (item.TryGetProperty("name", out JsonElement nameElement) &&
                nameElement.GetString() is { Length: > 0 } name)
            {
                accumulator.Name = name;
            }

            if (item.TryGetProperty("id", out JsonElement idElement))
            {
                accumulator.ItemId = idElement.GetString();
            }
        }

        if (accumulator.Emitted)
        {
            yield break;
        }

        yield return CreateFunctionCallUpdate(accumulator, responseId, modelId);
        accumulator.Emitted = true;
    }

    private static ChatResponseUpdate CreateFunctionCallUpdate(
        FunctionCallAccumulator accumulator,
        string? responseId,
        string? modelId)
    {
        string callId = string.IsNullOrEmpty(accumulator.ItemId)
            ? accumulator.CallId
            : $"{accumulator.CallId}|{accumulator.ItemId}";

        IDictionary<string, object?>? arguments = ParseArguments(accumulator.ArgumentsJson.ToString());
        FunctionCallContent content = new(callId, accumulator.Name, arguments);
        return new ChatResponseUpdate(ChatRole.Assistant, [content])
        {
            ModelId = modelId,
            ResponseId = responseId,
        };
    }

    private static (string CallId, string? ItemId) SplitFunctionCallId(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return ("call_unknown", null);
        }

        int bar = raw.IndexOf('|');
        if (bar < 0)
        {
            return (raw, null);
        }

        return (raw[..bar], raw[(bar + 1)..]);
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return "{}";
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteArgumentsObject(writer, arguments);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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
            case byte number:
                writer.WriteNumberValue(number);
                break;
            case short number:
                writer.WriteNumberValue(number);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case IDictionary<string, object?> dict:
                WriteArgumentsObject(writer, dict);
                break;
            case System.Collections.IEnumerable enumerable when value is not string:
                writer.WriteStartArray();
                foreach (object? item in enumerable)
                {
                    WriteJsonValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static IDictionary<string, object?>? ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?> { ["raw"] = json };
            }

            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = ConvertJsonElement(property.Value);
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["raw"] = json };
        }
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long l) => l,
            JsonValueKind.Number when element.TryGetDouble(out double d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(static p => p.Name, static p => ConvertJsonElement(p.Value), StringComparer.Ordinal),
            _ => element.GetRawText()
        };

    private static string FormatToolResult(FunctionResultContent result)
    {
        if (result.Exception is not null)
        {
            return result.Exception.Message;
        }

        return result.Result switch
        {
            null => string.Empty,
            string text => text,
            JsonElement element => element.GetRawText(),
            _ => result.Result.ToString() ?? string.Empty
        };
    }

    private static string FormatHttpError(HttpResponseMessage response, string body)
    {
        int status = (int)response.StatusCode;
        string trimmed = body.Length > 500 ? body[..500] + "…" : body;
        return status switch
        {
            401 => $"OpenAI Codex authentication failed (401). Re-run 'winharness login --provider openai' or check tokens. {trimmed}",
            403 => $"OpenAI Codex rejected the request (403). Subscription may be expired or the grant revoked. {trimmed}",
            429 => $"OpenAI Codex rate limit (429). Wait and retry. {trimmed}",
            _ => $"OpenAI Codex request failed ({status} {response.ReasonPhrase}). {trimmed}"
        };
    }

    private sealed class FunctionCallAccumulator
    {
        public FunctionCallAccumulator(string callId, string? itemId, string name, string arguments)
        {
            CallId = callId;
            ItemId = itemId;
            Name = name;
            ArgumentsJson = new StringBuilder(arguments);
        }

        public string CallId { get; }

        public string? ItemId { get; set; }

        public string Name { get; set; }

        public StringBuilder ArgumentsJson { get; }

        public bool Emitted { get; set; }
    }
}
