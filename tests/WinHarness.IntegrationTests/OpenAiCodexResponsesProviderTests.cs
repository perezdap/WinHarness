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
public sealed class OpenAiCodexResponsesProviderTests
{
    [TestMethod]
    public async Task StreamsTextAndUsageFromResponsesSse()
    {
        await using FakeCodexServer server = await FakeCodexServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        FakeCredentialStore store = new(CreateTokenSet());
        OpenAiCompatibleProviderFactory factory = new(options, store, [new OpenAiCodexOAuthFlow(new HttpClient())]);
        IChatProvider provider = factory.Create("openai", "gpt-5.4");
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

        StringAssert.Contains(streamed.ToString(), "Hello from Codex");
        Assert.IsNotNull(usage);
        Assert.AreEqual(20L, usage!.InputTokenCount);
        Assert.AreEqual(7L, usage.OutputTokenCount);
        StringAssert.Contains(server.LastRequestBody!, "\"model\":\"gpt-5.4\"");
        StringAssert.Contains(server.LastRequestBody!, "\"stream\":true");
        StringAssert.Contains(server.LastRequestBody!, "\"instructions\"");
        Assert.IsTrue(server.LastHeaders.ContainsKey("Authorization"));
        Assert.AreEqual("acct-test", server.LastHeaders["chatgpt-account-id"]);
        Assert.AreEqual("winharness", server.LastHeaders["originator"]);
    }

    [TestMethod]
    public async Task StreamsFunctionCallsAsToolUse()
    {
        await using FakeCodexServer server = await FakeCodexServer.StartToolUseAsync(CancellationToken.None);
        WinHarnessOptions options = CreateOptions(server.Endpoint.ToString());
        FakeCredentialStore store = new(CreateTokenSet());
        OpenAiCompatibleProviderFactory factory = new(options, store, [new OpenAiCodexOAuthFlow(new HttpClient())]);
        using IChatClient client = factory.Create("openai", "gpt-5.4").CreateChatClient();

        List<FunctionCallContent> calls = [];
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           "Use a tool.",
                           cancellationToken: CancellationToken.None))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual("get_weather", calls[0].Name);
        Assert.AreEqual("call_1|fc_1", calls[0].CallId);
        Assert.IsNotNull(calls[0].Arguments);
        Assert.AreEqual("SF", calls[0].Arguments!["location"]?.ToString());
    }

    [TestMethod]
    public void ValidatorAcceptsOpenAiCodexResponsesKind()
    {
        WinHarnessOptions options = CreateOptions("https://chatgpt.com/backend-api");
        WinHarness.Infrastructure.Configuration.WinHarnessOptionsValidator.Validate(options);
    }

    [TestMethod]
    public void FactoryRoutesOpenAiCodexKind()
    {
        WinHarnessOptions options = CreateOptions("https://chatgpt.com/backend-api");
        FakeCredentialStore store = new(CreateTokenSet());
        OpenAiCompatibleProviderFactory factory = new(options, store, [new OpenAiCodexOAuthFlow(new HttpClient())]);
        IChatProvider provider = factory.Create("openai", "gpt-5.4");
        Assert.AreEqual("openai", provider.ProviderId);
        using IChatClient client = provider.CreateChatClient();
        Assert.IsInstanceOfType(client, typeof(IChatClient));
    }

    private static OAuthTokenSet CreateTokenSet() =>
        new(
            AccessToken: "access-token",
            RefreshToken: "refresh-token",
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
            BaseUrl: OpenAiCodexOAuthFlow.DefaultBaseUrl,
            AccountId: "acct-test");

    private static WinHarnessOptions CreateOptions(string baseUrl)
    {
        WinHarnessOptions options = new();
        ProviderOptions provider = new()
        {
            Id = "openai",
            Kind = "openai-codex-responses",
            BaseUrl = baseUrl.TrimEnd('/'),
            Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "openai" }
        };
        provider.Models.Add(new ModelOptions
        {
            Id = "gpt-5.4",
            ProviderModelId = "gpt-5.4",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: true,
                Vision: true,
                PromptCaching: true,
                StructuredOutput: false,
                Reasoning: true)
        });
        options.Providers.Add(provider);
        return options;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly string _tokenJson;

        public FakeCredentialStore(OAuthTokenSet tokens)
        {
            _tokenJson = JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet);
        }

        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(_tokenJson);

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([OAuthCredentialNames.ForProvider("openai")]);
    }

    private sealed class FakeCodexServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;
        private readonly string _sseBody;

        private FakeCodexServer(TcpListener listener, Task serverTask, string sseBody)
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

        public static Task<FakeCodexServer> StartAsync(CancellationToken cancellationToken)
        {
            const string body = """
                event: response.created
                data: {"type":"response.created","response":{"id":"resp_1","model":"gpt-5.4"}}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","delta":"Hello from "}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","delta":"Codex"}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"resp_1","model":"gpt-5.4","usage":{"input_tokens":20,"output_tokens":7,"total_tokens":27}}}

                """;
            return StartCoreAsync(body, cancellationToken);
        }

        public static Task<FakeCodexServer> StartToolUseAsync(CancellationToken cancellationToken)
        {
            const string body = """
                event: response.output_item.added
                data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":""}}

                event: response.function_call_arguments.delta
                data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"{\"location\":"}

                event: response.function_call_arguments.delta
                data: {"type":"response.function_call_arguments.delta","output_index":0,"delta":"\"SF\"}"}

                event: response.output_item.done
                data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"get_weather","arguments":"{\"location\":\"SF\"}"}}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"resp_2","usage":{"input_tokens":10,"output_tokens":4,"total_tokens":14}}}

                """;
            return StartCoreAsync(body, cancellationToken);
        }

        private static Task<FakeCodexServer> StartCoreAsync(string body, CancellationToken cancellationToken)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            TaskCompletionSource<FakeCodexServer> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = Task.Run(async () =>
            {
                FakeCodexServer server = await ready.Task.ConfigureAwait(false);
                await server.AcceptOneRequestAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            FakeCodexServer fake = new(listener, task, body);
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
