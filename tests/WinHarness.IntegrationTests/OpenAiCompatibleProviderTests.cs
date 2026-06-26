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
public sealed class OpenAiCompatibleProviderTests
{
    [TestMethod]
    public async Task StreamsFromOpenAiCompatibleEndpoint()
    {
        await using FakeOpenAiServer server = await FakeOpenAiServer.StartAsync(CancellationToken.None);
        WinHarnessOptions options = new();
        ProviderOptions provider = new()
        {
            Id = "fake",
            Kind = "openai-compatible",
            BaseUrl = server.Endpoint.ToString(),
            CredentialName = "WinHarness:test"
        };
        provider.Models.Add(new ModelOptions
        {
            Id = "chat",
            ProviderModelId = "gpt-fake",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: true,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: true,
                Reasoning: false)
        });
        options.Providers.Add(provider);

        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());
        IChatProvider chatProvider = factory.Create("fake", "chat");
        using IChatClient client = chatProvider.CreateChatClient();

        StringBuilder streamed = new();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           "Say hello.",
                           cancellationToken: CancellationToken.None))
        {
            streamed.Append(update);
        }

        StringAssert.Contains(streamed.ToString(), "WinHarness");
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>("fake-key");
        }

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(["WinHarness:test"]);
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serverTask;

        private FakeOpenAiServer(TcpListener listener, Task serverTask)
        {
            _listener = listener;
            _serverTask = serverTask;
        }

        public Uri Endpoint
        {
            get
            {
                IPEndPoint endpoint = (IPEndPoint)_listener.LocalEndpoint;
                return new Uri($"http://127.0.0.1:{endpoint.Port}/v1");
            }
        }

        public static Task<FakeOpenAiServer> StartAsync(CancellationToken cancellationToken)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();

            TaskCompletionSource<FakeOpenAiServer> serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task task = Task.Run(async () =>
            {
                FakeOpenAiServer server = await serverReady.Task.ConfigureAwait(false);
                await server.AcceptOneRequestAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken);

            FakeOpenAiServer fake = new(listener, task);
            serverReady.SetResult(fake);
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

            const string body = """
                data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":0,"model":"gpt-fake","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello from "},"finish_reason":null}]}

                data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":0,"model":"gpt-fake","choices":[{"index":0,"delta":{"content":"WinHarness."},"finish_reason":"stop"}]}

                data: [DONE]

                """;
            string response = string.Concat(
                "HTTP/1.1 200 OK\r\n",
                "Content-Type: text/event-stream; charset=utf-8\r\n",
                "Connection: close\r\n",
                "\r\n",
                body);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), cancellationToken).ConfigureAwait(false);
        }

        private static async Task ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                total += read;
                if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0)
                {
                    return;
                }
            }
        }
    }
}
