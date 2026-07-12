using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Serialization;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class AnthropicMessagesProviderTests
{
    [TestMethod]
    public async Task StreamsTextAndUsageFromMessagesSse()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());
        IChatProvider provider = factory.Create("anthropic", "sonnet");
        using IChatClient client = provider.CreateChatClient();

        StringBuilder streamed = new();
        UsageDetails? usage = null;
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           "Say hello.",
                           cancellationToken: CancellationToken.None))
        {
            streamed.Append(update.Text);
            usage = update.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;
        }

        StringAssert.Contains(streamed.ToString(), "Hello from Anthropic");
        Assert.IsNotNull(usage);
        Assert.AreEqual(12L, usage!.InputTokenCount);
        Assert.AreEqual(5L, usage.OutputTokenCount);
        StringAssert.Contains(server.LastRequestBody!, "\"model\":\"claude-sonnet-4-20250514\"");
        StringAssert.Contains(server.LastRequestBody!, "\"stream\":true");
        Assert.IsTrue(server.LastHeaders.ContainsKey("x-api-key"));
    }

    [TestMethod]
    public async Task StreamsToolUseBlocksAsFunctionCalls()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartToolUseAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());
        using IChatClient client = factory.Create("anthropic", "sonnet").CreateChatClient();

        List<FunctionCallContent> calls = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           "Use a tool.",
                           cancellationToken: CancellationToken.None))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("get_weather", calls[0].Name);
        Assert.AreEqual("toolu_1", calls[0].CallId);
        Assert.IsNotNull(calls[0].Arguments);
        Assert.AreEqual("SF", calls[0].Arguments!["location"]?.ToString());
    }

    [TestMethod]
    public async Task CoalescesConsecutiveToolResultsIntoSingleUserTurn()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        using IChatClient client = new OpenAiCompatibleProviderFactory(options, new FakeCredentialStore())
            .Create("anthropic", "sonnet")
            .CreateChatClient();

        List<ChatMessage> history =
        [
            new(ChatRole.User, "What's the weather?"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("toolu_1", "get_weather", new Dictionary<string, object?> { ["location"] = "SF" }),
                new FunctionCallContent("toolu_2", "get_weather", new Dictionary<string, object?> { ["location"] = "NYC" }),
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_1", "sunny")]),
            new(ChatRole.Tool, [new FunctionResultContent("toolu_2", "rainy")]),
            new(ChatRole.User, "Summarize both."),
        ];

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(history, cancellationToken: CancellationToken.None))
        {
        }

        Assert.IsNotNull(server.LastRequestBody);
        using JsonDocument document = JsonDocument.Parse(server.LastRequestBody!);
        JsonElement messages = document.RootElement.GetProperty("messages");
        Assert.AreEqual(3, messages.GetArrayLength(), "user + assistant + coalesced tool/user turn");

        JsonElement last = messages[2];
        Assert.AreEqual("user", last.GetProperty("role").GetString());
        JsonElement content = last.GetProperty("content");
        Assert.AreEqual(3, content.GetArrayLength());
        Assert.AreEqual("tool_result", content[0].GetProperty("type").GetString());
        Assert.AreEqual("toolu_1", content[0].GetProperty("tool_use_id").GetString());
        Assert.AreEqual("tool_result", content[1].GetProperty("type").GetString());
        Assert.AreEqual("toolu_2", content[1].GetProperty("tool_use_id").GetString());
        Assert.AreEqual("text", content[2].GetProperty("type").GetString());
        Assert.AreEqual("Summarize both.", content[2].GetProperty("text").GetString());

        // Strict role alternation: user, assistant, user
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("assistant", messages[1].GetProperty("role").GetString());
    }

    [TestMethod]
    public async Task RaisesMaxTokensWhenThinkingBudgetWouldExceedIt()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        using IChatClient client = new OpenAiCompatibleProviderFactory(options, new FakeCredentialStore())
            .Create("anthropic", "sonnet")
            .CreateChatClient();

        ChatOptions chatOptions = new()
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh }
        };

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                           "Think hard.",
                           chatOptions,
                           CancellationToken.None))
        {
        }

        Assert.IsNotNull(server.LastRequestBody);
        using JsonDocument document = JsonDocument.Parse(server.LastRequestBody!);
        int maxTokens = document.RootElement.GetProperty("max_tokens").GetInt32();
        int budget = document.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
        Assert.AreEqual(32_000, budget);
        Assert.IsTrue(budget < maxTokens, $"budget {budget} must be < max_tokens {maxTokens}");
    }

    [TestMethod]
    public async Task RoundTripsThinkingSignatureOnFollowUpRequest()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartThinkingAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        using IChatClient client = new OpenAiCompatibleProviderFactory(options, new FakeCredentialStore())
            .Create("anthropic", "sonnet")
            .CreateChatClient();

        List<ChatMessage> turn = [new(ChatRole.User, "Reason then answer.")];
        List<AIContent> assistantContents = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(turn, cancellationToken: CancellationToken.None))
        {
            assistantContents.AddRange(update.Contents);
        }

        TextReasoningContent? reasoning = assistantContents.OfType<TextReasoningContent>().SingleOrDefault();
        Assert.IsNotNull(reasoning);
        Assert.AreEqual("stepwise", reasoning!.Text);
        Assert.IsNotNull(reasoning.AdditionalProperties);
        Assert.AreEqual(
            "sig_abc",
            reasoning.AdditionalProperties!["anthropic_thinking_signature"]?.ToString());

        // Second request: replay assistant thinking + ask follow-up.
        await using FakeAnthropicServer followUp = await FakeAnthropicServer.StartAsync(CancellationToken.None);
        options = CreateOptions(followUp.Endpoint.ToString());
        using IChatClient followUpClient = new OpenAiCompatibleProviderFactory(options, new FakeCredentialStore())
            .Create("anthropic", "sonnet")
            .CreateChatClient();

        List<ChatMessage> history =
        [
            new(ChatRole.User, "Reason then answer."),
            new(ChatRole.Assistant, assistantContents.Where(c => c is not UsageContent).ToList()),
            new(ChatRole.User, "Continue."),
        ];

        await foreach (ChatResponseUpdate _ in followUpClient.GetStreamingResponseAsync(
                           history,
                           cancellationToken: CancellationToken.None))
        {
        }

        Assert.IsNotNull(followUp.LastRequestBody);
        StringAssert.Contains(followUp.LastRequestBody!, "\"type\":\"thinking\"");
        StringAssert.Contains(followUp.LastRequestBody!, "\"signature\":\"sig_abc\"");
        StringAssert.Contains(followUp.LastRequestBody!, "\"thinking\":\"stepwise\"");
    }

    [TestMethod]
    public async Task OAuthProviderSendsBearerAndBetaHeaders()
    {
        await using FakeAnthropicServer server = await FakeAnthropicServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOAuthOptions(server.Endpoint.ToString());
        OAuthTokenSet tokens = new(
            AccessToken: "oauth-access",
            RefreshToken: "oauth-refresh",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1));
        OpenAiCompatibleProviderFactory factory = new(
            options,
            new FakeCredentialStore(JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet)),
            [new AnthropicOAuthFlow(new HttpClient())]);
        using IChatClient client = factory.Create("anthropic", "sonnet").CreateChatClient();

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
                           "hi",
                           cancellationToken: CancellationToken.None))
        {
        }

        Assert.IsTrue(server.LastHeaders.TryGetValue("Authorization", out string? auth));
        StringAssert.StartsWith(auth!, "Bearer ");
        Assert.IsTrue(server.LastHeaders.ContainsKey("anthropic-beta"));
        Assert.IsFalse(server.LastHeaders.ContainsKey("x-api-key"));
    }

    [TestMethod]
    public void ValidatorAcceptsAnthropicMessagesKind()
    {
        WinHarnessOptions options = CreateOptions("https://api.anthropic.com");
        WinHarness.Infrastructure.Configuration.WinHarnessOptionsValidator.Validate(options);
    }

    [TestMethod]
    public void FactoryRoutesAnthropicKind()
    {
        WinHarnessOptions options = CreateOptions("https://api.anthropic.com");
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());
        IChatProvider provider = factory.Create("anthropic", "sonnet");
        Assert.AreEqual("anthropic", provider.ProviderId);
        using IChatClient client = provider.CreateChatClient();
        Assert.IsInstanceOfType(client, typeof(IChatClient));
    }

    private static WinHarnessOptions CreateOptions(string baseUrl)
    {
        WinHarnessOptions options = new();
        ProviderOptions provider = new()
        {
            Id = "anthropic",
            Kind = "anthropic-messages",
            BaseUrl = baseUrl.TrimEnd('/'),
            CredentialName = "WinHarness:anthropic"
        };
        provider.Models.Add(new ModelOptions
        {
            Id = "sonnet",
            ProviderModelId = "claude-sonnet-4-20250514",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: true,
                Vision: false,
                PromptCaching: true,
                StructuredOutput: false,
                Reasoning: true)
        });
        options.Providers.Add(provider);
        return options;
    }

    private static WinHarnessOptions CreateOAuthOptions(string baseUrl)
    {
        WinHarnessOptions options = CreateOptions(baseUrl);
        ProviderOptions provider = options.Providers[0];
        provider.CredentialName = null;
        provider.Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "anthropic" };
        return options;
    }

    private sealed class FakeCredentialStore(string? secret = "test-key") : ICredentialStore
    {
        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.FromResult(secret);

        public ValueTask SetSecretAsync(string targetName, string secretValue, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(["WinHarness:anthropic", "WinHarness:oauth:anthropic"]);
    }

    private sealed class FakeAnthropicServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;
        private readonly string _sseBody;

        private FakeAnthropicServer(TcpListener listener, Task serverTask, string sseBody)
        {
            _listener = listener;
            _serverTask = serverTask;
            _sseBody = sseBody;
        }

        public Uri Endpoint
        {
            get
            {
                IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
                return new Uri($"http://127.0.0.1:{endpoint.Port}");
            }
        }

        public string? LastRequestBody { get; private set; }

        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static Task<FakeAnthropicServer> StartAsync(CancellationToken cancellationToken)
        {
            const string body = """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-20250514","usage":{"input_tokens":12,"output_tokens":1}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello from "}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Anthropic"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}

                event: message_stop
                data: {"type":"message_stop"}

                """;
            return StartCoreAsync(body, cancellationToken);
        }

        public static Task<FakeAnthropicServer> StartToolUseAsync(CancellationToken cancellationToken)
        {
            const string body = """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-20250514","usage":{"input_tokens":20,"output_tokens":1}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{}}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"location\":"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":" \"SF\"}"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":18}}

                event: message_stop
                data: {"type":"message_stop"}

                """;
            return StartCoreAsync(body, cancellationToken);
        }

        public static Task<FakeAnthropicServer> StartThinkingAsync(CancellationToken cancellationToken)
        {
            const string body = """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_3","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-20250514","usage":{"input_tokens":8,"output_tokens":1}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"step"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"wise"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig_abc"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: content_block_start
                data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"done"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":1}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":12}}

                event: message_stop
                data: {"type":"message_stop"}

                """;
            return StartCoreAsync(body, cancellationToken);
        }

        private static Task<FakeAnthropicServer> StartCoreAsync(string body, CancellationToken cancellationToken)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            TaskCompletionSource<FakeAnthropicServer> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = Task.Run(async () =>
            {
                FakeAnthropicServer server = await ready.Task.ConfigureAwait(false);
                await server.AcceptOneRequestAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            FakeAnthropicServer fake = new(listener, task, body);
            ready.SetResult(fake);
            return Task.FromResult(fake);
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
        }

        private async Task AcceptOneRequestAsync(CancellationToken cancellationToken)
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using NetworkStream stream = client.GetStream();
            await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);

            string response = string.Concat(
                "HTTP/1.1 200 OK\r\n",
                "Content-Type: text/event-stream; charset=utf-8\r\n",
                "Connection: close\r\n",
                "\r\n",
                _sseBody);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
        }

        private async Task ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16_384];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                int headerEnd = buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8);
                if (headerEnd < 0)
                {
                    continue;
                }

                string headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
                foreach (string line in headerText.Split("\r\n"))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        LastHeaders[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                int contentLength = 0;
                if (LastHeaders.TryGetValue("Content-Length", out string? lengthText))
                {
                    _ = int.TryParse(lengthText, out contentLength);
                }

                int bodyStart = headerEnd + 4;
                while (total - bodyStart < contentLength && total < buffer.Length)
                {
                    int more = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                        .ConfigureAwait(false);
                    if (more == 0)
                    {
                        break;
                    }

                    total += more;
                }

                if (contentLength > 0)
                {
                    LastRequestBody = Encoding.UTF8.GetString(buffer, bodyStart, Math.Min(contentLength, total - bodyStart));
                }

                return;
            }
        }
    }
}
