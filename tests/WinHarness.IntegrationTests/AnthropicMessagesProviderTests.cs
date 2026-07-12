using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;

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

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("test-key");

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(["WinHarness:anthropic"]);
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
