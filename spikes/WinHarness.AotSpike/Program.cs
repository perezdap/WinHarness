using System.ClientModel;
using System.Collections.ObjectModel;
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
using ModelContextProtocol.Protocol;
using OpenAI;
using OpenAI.Chat;
using Spectre.Console;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

var app = ConsoleApp.Create();

if (args is ["mcp-server"])
{
    await FakeMcpServer.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None)
        .ConfigureAwait(false);
    return;
}

app.Add("", async (CancellationToken cancellationToken) =>
{
    await AotSpikeRunner.RunAsync(cancellationToken).ConfigureAwait(false);
});

app.Add("tui-smoke", () =>
{
    AotSpikeRunner.RunTuiSmoke();
});

await app.RunAsync(args).ConfigureAwait(false);

internal static class AotSpikeRunner
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        SpikeOptions options = ExerciseHostingAndConfiguration();
        ExerciseSourceGeneratedJson(options);
        ExerciseSpectreConsole(options);
        ExerciseTerminalGui();

        await ExerciseOpenAiCompatibleStreamingAsync(cancellationToken).ConfigureAwait(false);
        await ExerciseMcpToolsAsync(cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine("[green]WinHarness AOT spike completed successfully.[/]");
    }

    public static void RunTuiSmoke()
    {
        ExerciseTerminalGui();
        AnsiConsole.MarkupLine("[green]Terminal.Gui smoke completed successfully.[/]");
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

    private static void ExerciseTerminalGui()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);

        using Window window = new()
        {
            Title = "WinHarness TUI AOT Smoke",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        Label status = new()
        {
            Text = "provider local-ollama · model local-coder",
            X = 1,
            Y = 0,
            Width = Dim.Fill()
        };
        ObservableCollection<string> transcriptLines = ["user: hello", "assistant: hello from WinHarness"];
        ListView transcript = new()
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill()! - 2,
            Height = Dim.Fill()! - 4
        };
        transcript.SetSource(transcriptLines);
        TextField input = new()
        {
            Text = "Type a prompt...",
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill()! - 2
        };

        window.Add(status, transcript, input);
        SessionToken? token = app.Begin(window);
        if (token is null)
        {
            throw new InvalidOperationException("Terminal.Gui did not start a session.");
        }

        app.Invoke(() =>
        {
            transcriptLines.Add("tool: spike rendered");
            transcript.SetSource(transcriptLines);
            input.Text = string.Empty;
        });
        app.End(token);
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

    private static async Task ExerciseMcpToolsAsync(CancellationToken cancellationToken)
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

        CallToolResult result = await client.CallToolAsync(
            "spike_echo",
            new Dictionary<string, object?> { ["message"] = "Hello MCP!" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string text = string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(static block => block.Text));
        if (result.IsError is true || !text.Contains("Hello MCP!", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("MCP tools/call did not return the expected echo result.");
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
            McpMessage? request = await ReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(request.Body);
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
                "tools/call" => CreateToolCallResponse(id.Value, root),
                _ => CreateEmptyResultResponse(id.Value)
            };

            await WriteMessageAsync(output, response, request.UseContentLengthHeaders, cancellationToken)
                .ConfigureAwait(false);
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
            ",\"result\":{\"tools\":[{\"name\":\"spike_echo\",\"description\":\"A fake AOT spike MCP tool.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}},\"required\":[\"message\"]}},{\"name\":\"spike_fail\",\"description\":\"A fake failing AOT spike MCP tool.\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\"}}}}]}}");
    }

    private static string CreateToolCallResponse(JsonElement id, JsonElement request)
    {
        request.TryGetProperty("params", out JsonElement parameters);
        string toolName = parameters.TryGetProperty("name", out JsonElement toolNameElement)
            ? toolNameElement.GetString() ?? string.Empty
            : string.Empty;
        string message = parameters.TryGetProperty("arguments", out JsonElement arguments) &&
            arguments.TryGetProperty("message", out JsonElement messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;

        if (string.Equals(toolName, "spike_fail", StringComparison.Ordinal))
        {
            string escapedError = JsonSerializer.Serialize("error: " + message, SpikeJsonSerializerContext.Default.String);
            return string.Concat(
                "{\"jsonrpc\":\"2.0\",\"id\":",
                id.GetRawText(),
                ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":",
                escapedError,
                "}],\"isError\":true}}");
        }

        string escapedMessage = JsonSerializer.Serialize("echo: " + message, SpikeJsonSerializerContext.Default.String);
        return string.Concat(
            "{\"jsonrpc\":\"2.0\",\"id\":",
            id.GetRawText(),
            ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":",
            escapedMessage,
            "}],\"isError\":false}}");
    }

    private static string CreateEmptyResultResponse(JsonElement id)
    {
        return string.Concat("{\"jsonrpc\":\"2.0\",\"id\":", id.GetRawText(), ",\"result\":{}}");
    }

    private static async Task<McpMessage?> ReadMessageAsync(Stream input, CancellationToken cancellationToken)
    {
        string? firstLine = await ReadAsciiLineAsync(input, cancellationToken).ConfigureAwait(false);
        if (firstLine is null)
        {
            return null;
        }

        if (!firstLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
        {
            return new McpMessage(firstLine, UseContentLengthHeaders: false);
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

        return new McpMessage(Encoding.UTF8.GetString(body), UseContentLengthHeaders: true);
    }

    private static async Task WriteMessageAsync(
        Stream output,
        string body,
        bool useContentLengthHeaders,
        CancellationToken cancellationToken)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

        if (useContentLengthHeaders)
        {
            byte[] headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n");
            await output.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        }

        await output.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        if (!useContentLengthHeaders)
        {
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }

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

    private sealed record McpMessage(string Body, bool UseContentLengthHeaders);
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
[JsonSerializable(typeof(string))]
internal sealed partial class SpikeJsonSerializerContext : JsonSerializerContext;
