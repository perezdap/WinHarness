using System.ClientModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using Spectre.Console;

var app = ConsoleApp.Create();

app.Add("", async (CancellationToken cancellationToken) =>
{
    await AotSpikeRunner.RunAsync(cancellationToken).ConfigureAwait(false);
});

app.Add("mcp-server", async (CancellationToken cancellationToken) =>
{
    await FakeMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cancellationToken)
        .ConfigureAwait(false);
});

await app.RunAsync(args).ConfigureAwait(false);

internal static class AotSpikeRunner
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        SpikeOptions options = ExerciseHostingAndConfiguration();
        ExerciseSourceGeneratedJson(options);
        ExerciseSpectreConsole(options);

        await ExerciseOpenAiCompatibleStreamingAsync(cancellationToken).ConfigureAwait(false);
        await ExerciseMcpToolsListAsync(cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine("[green]WinHarness AOT spike completed successfully.[/]");
    }

    private static SpikeOptions ExerciseHostingAndConfiguration()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spike:Name"] = "WinHarness",
            ["Spike:Message"] = "Native AOT dependency spike",
            ["Spike:RepeatCount"] = "2"
        });

        SpikeOptions options = new();
        builder.Configuration.GetSection("Spike").Bind(options);
        builder.Services.AddSingleton(options);

        using IHost host = builder.Build();
        return host.Services.GetRequiredService<SpikeOptions>();
    }

    private static void ExerciseSourceGeneratedJson(SpikeOptions options)
    {
        SpikePayload payload = new(options.Name, options.Message, DateTimeOffset.UnixEpoch);
        string json = JsonSerializer.Serialize(payload, SpikeJsonSerializerContext.Default.SpikePayload);
        SpikePayload? roundTrip = JsonSerializer.Deserialize(json, SpikeJsonSerializerContext.Default.SpikePayload);

        if (roundTrip is null || roundTrip.Name != options.Name)
        {
            throw new InvalidOperationException("Source-generated JSON round trip failed.");
        }
    }

    private static void ExerciseSpectreConsole(SpikeOptions options)
    {
        Table table = new Table()
            .Title("WinHarness Phase 0")
            .AddColumn("Path")
            .AddColumn("Status");

        table.AddRow("Configuration", Markup.Escape(options.Message));
        table.AddRow("Spectre.Console", "[green]rendered[/]");

        AnsiConsole.Write(table);
    }

    private static async Task ExerciseOpenAiCompatibleStreamingAsync(CancellationToken cancellationToken)
    {
        await using FakeOpenAiServer server = await FakeOpenAiServer.StartAsync(cancellationToken).ConfigureAwait(false);

        ChatClient chatClient = new(
            model: "gpt-4o-mini",
            credential: new ApiKeyCredential("winharness-spike-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = server.Endpoint
            });

        IChatClient client = chatClient.AsIChatClient();

        StringBuilder streamed = new();
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           "Say hello from WinHarness.",
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            streamed.Append(update);
        }

        if (!streamed.ToString().Contains("WinHarness", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"OpenAI-compatible streaming path returned unexpected text: {streamed}");
        }
    }

    private static async Task ExerciseMcpToolsListAsync(CancellationToken cancellationToken)
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");

        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "winharness-aot-spike-fake",
            Command = executablePath,
            Arguments = ["mcp-server"],
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        });

        await using McpClient client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!tools.Any(static tool => tool.Name == "spike_echo"))
        {
            throw new InvalidOperationException("MCP tools/list did not return the fake spike_echo tool.");
        }
    }
}

internal sealed class FakeOpenAiServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _shutdown = new();

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
        Task serverTask = Task.Run(async () =>
        {
            FakeOpenAiServer server = await serverReady.Task.ConfigureAwait(false);

            await server.AcceptOneRequestAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

        FakeOpenAiServer server = new(listener, serverTask);
        serverReady.SetResult(server);
        return Task.FromResult(server);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();

        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task AcceptOneRequestAsync(CancellationToken cancellationToken)
    {
        using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();

        await ReadHttpRequestAsync(stream, cancellationToken).ConfigureAwait(false);

        const string responseBody = """
            data: {"id":"chatcmpl-spike","object":"chat.completion.chunk","created":0,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello from "},"finish_reason":null}]}

            data: {"id":"chatcmpl-spike","object":"chat.completion.chunk","created":0,"model":"gpt-4o-mini","choices":[{"index":0,"delta":{"content":"WinHarness."},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        string response = string.Concat(
            "HTTP/1.1 200 OK\r\n",
            "Content-Type: text/event-stream; charset=utf-8\r\n",
            "Cache-Control: no-cache\r\n",
            "Connection: close\r\n",
            "\r\n",
            responseBody);

        byte[] bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        int totalBytesRead = 0;

        while (totalBytesRead < buffer.Length)
        {
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
            if (HeaderTerminatorIndex(buffer.AsSpan(0, totalBytesRead)) >= 0)
            {
                break;
            }
        }
    }

    private static int HeaderTerminatorIndex(ReadOnlySpan<byte> buffer)
    {
        ReadOnlySpan<byte> terminator = "\r\n\r\n"u8;
        return buffer.IndexOf(terminator);
    }
}

internal static class FakeMcpServer
{
    public static async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? request = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("method", out JsonElement methodElement))
            {
                continue;
            }

            string? method = methodElement.GetString();
            JsonElement? id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.Clone() : null;

            if (id is null)
            {
                continue;
            }

            string response = method switch
            {
                "initialize" => CreateInitializeResponse(id.Value),
                "tools/list" => CreateToolsListResponse(id.Value),
                _ => CreateEmptyResultResponse(id.Value)
            };

            await WriteMessageAsync(output, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreateInitializeResponse(JsonElement id)
    {
        return string.Concat(
            "{\"jsonrpc\":\"2.0\",\"id\":",
            id.GetRawText(),
            ",\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"winharness-aot-spike-fake\",\"version\":\"0.1.0\"}}}");
    }

    private static string CreateToolsListResponse(JsonElement id)
    {
        return string.Concat(
            "{\"jsonrpc\":\"2.0\",\"id\":",
            id.GetRawText(),
            ",\"result\":{\"tools\":[{\"name\":\"spike_echo\",\"description\":\"A fake AOT spike MCP tool.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}}]}}");
    }

    private static string CreateEmptyResultResponse(JsonElement id)
    {
        return string.Concat("{\"jsonrpc\":\"2.0\",\"id\":", id.GetRawText(), ",\"result\":{}}");
    }

    private static async Task<string?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        string? firstLine = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
        if (firstLine is null)
        {
            return null;
        }

        if (!firstLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            return firstLine;
        }

        int contentLength = int.Parse(firstLine["Content-Length:".Length..].Trim(), provider: null);

        while (true)
        {
            string? line = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }
        }

        byte[] body = new byte[contentLength];
        int offset = 0;
        while (offset < body.Length)
        {
            int bytesRead = await input.ReadAsync(body.AsMemory(offset, body.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                return null;
            }

            offset += bytesRead;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static async Task WriteMessageAsync(Stream output, string body, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        byte[] headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");

        await output.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream input, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[256];
        int length = 0;

        while (length < buffer.Length)
        {
            byte[] one = new byte[1];
            int bytesRead = await input.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                return length == 0 ? null : Encoding.ASCII.GetString(buffer, 0, length);
            }

            byte value = one[0];
            if (value == (byte)'\n')
            {
                if (length > 0 && buffer[length - 1] == (byte)'\r')
                {
                    length--;
                }

                return Encoding.ASCII.GetString(buffer, 0, length);
            }

            buffer[length++] = value;
        }

        throw new InvalidOperationException("MCP header line exceeded the supported spike length.");
    }
}

internal sealed class SpikeOptions
{
    public string Name { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public int RepeatCount { get; set; }
}

internal sealed record SpikePayload(string Name, string Message, DateTimeOffset Timestamp);

[JsonSerializable(typeof(SpikeOptions))]
[JsonSerializable(typeof(SpikePayload))]
internal sealed partial class SpikeJsonSerializerContext : JsonSerializerContext;
