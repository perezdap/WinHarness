using System.Buffers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using WinHarness;
using WinHarness.Configuration;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Mcp;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Tools;

const string Version = "0.1.0";

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine(Version);
    return;
}

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Configuration.AddWinHarnessConfiguration();
hostBuilder.Services.AddWinHarnessOptions(hostBuilder.Configuration);
hostBuilder.Services
    .AddWinHarnessInfrastructure()
    .AddWinHarnessPlatform()
    .AddWinHarnessCore()
    .AddWinHarnessProviders()
    .AddWinHarnessMcp()
    .AddWinHarnessTools();

using IHost host = hostBuilder.Build();
host.Services.GetRequiredService<IAnsiConsoleConfigurator>().EnableAnsi();

var app = ConsoleApp.Create();

app.Add("", () =>
{
    AnsiConsole.MarkupLine("[bold]WinHarness[/] v" + Version);
    AnsiConsole.MarkupLine("OpenAI-compatible, Windows-first AI coding harness.");
});

app.Add("version", () =>
{
    Console.WriteLine(Version);
});

app.Add("diagnostics aot", () =>
{
    Table table = new Table()
        .Title("WinHarness Diagnostics")
        .AddColumn("Check")
        .AddColumn("Status");

    table.AddRow(".NET", "10");
    table.AddRow("Native AOT", "[green]configured[/]");
    table.AddRow("Provider scope", "OpenAI-compatible");
    table.AddRow("Configuration", Markup.Escape(WinHarnessConfiguration.GetConfigurationDirectory()));

    AnsiConsole.Write(table);
});

app.Add("diagnostics write", async (string message, CancellationToken cancellationToken) =>
{
    IDiagnosticSink sink = host.Services.GetRequiredService<IDiagnosticSink>();
    await sink.WriteAsync(
        new DiagnosticRecord(
            DateTimeOffset.UtcNow,
            "cli",
            "manual",
            message,
            new Dictionary<string, string>
            {
                ["source"] = "winharness diagnostics write"
            }),
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Diagnostic record written.");
});

app.Add("config init", (bool overwrite = false) =>
{
    string directory = WinHarnessConfiguration.GetConfigurationDirectory();
    Directory.CreateDirectory(directory);

    string path = Path.Combine(directory, "config.json");
    if (File.Exists(path) && !overwrite)
    {
        Console.WriteLine($"Configuration already exists: {path}");
        return;
    }

    const string sample = """
        {
          "defaultProvider": "local-ollama",
          "defaultModel": "local-coder",
          "providers": [
            {
              "id": "local-ollama",
              "kind": "openai-compatible",
              "baseUrl": "http://localhost:11434/v1",
              "credentialName": null,
              "models": [
                {
                  "id": "local-coder",
                  "providerModelId": "qwen2.5-coder:latest",
                  "capabilities": {
                    "streaming": true,
                    "toolCalling": false,
                    "vision": false,
                    "promptCaching": false,
                    "structuredOutput": false,
                    "reasoning": false
                  }
                }
              ]
            }
          ],
          "mcpServers": []
        }
        """;

    File.WriteAllText(path, sample);
    Console.WriteLine($"Wrote {path}");
});

app.Add("chat", async (string prompt, string? providerId = null, string? modelId = null, CancellationToken cancellationToken = default) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    string resolvedProviderId = providerId ?? options.DefaultProvider;
    string resolvedModelId = modelId ?? options.DefaultModel;

    if (string.IsNullOrWhiteSpace(resolvedProviderId) || string.IsNullOrWhiteSpace(resolvedModelId))
    {
        throw new InvalidOperationException("Configure defaultProvider/defaultModel or pass --provider-id and --model-id.");
    }

    IAgentRuntime runtime = host.Services.GetRequiredService<IAgentRuntime>();
    await foreach (AgentEvent agentEvent in runtime.RunAsync(
                       new AgentRunRequest(resolvedProviderId, resolvedModelId, prompt),
                       cancellationToken).ConfigureAwait(false))
    {
        if (agentEvent.Kind == AgentEventKind.AssistantDelta)
        {
            Console.Write(agentEvent.Message);
        }
    }

    Console.WriteLine();
});

app.Add("tools list", async (CancellationToken cancellationToken) =>
{
    ToolRegistry registry = host.Services.GetRequiredService<ToolRegistry>();
    IReadOnlyDictionary<string, ITool> tools = await registry.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    foreach (ITool tool in tools.Values)
    {
        Console.WriteLine($"{tool.Name}\t{tool.Description}");
    }
});

app.Add("tools call", async (string name, string argumentsJson = "{}", CancellationToken cancellationToken = default) =>
{
    ToolRegistry registry = host.Services.GetRequiredService<ToolRegistry>();
    IReadOnlyDictionary<string, ITool> tools = await registry.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    if (!tools.TryGetValue(name, out ITool? tool))
    {
        throw new InvalidOperationException($"Tool '{name}' is not available.");
    }

    using JsonDocument arguments = JsonDocument.Parse(argumentsJson);
    ToolResult result = await tool.ExecuteAsync(
        new ToolInvocation(name, arguments.RootElement.Clone()),
        cancellationToken).ConfigureAwait(false);

    Console.WriteLine(result.Content);
    if (!result.Succeeded)
    {
        Environment.ExitCode = 1;
    }
});

app.Add("providers list", () =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    foreach (ProviderOptions provider in options.Providers)
    {
        Console.WriteLine($"{provider.Id}\t{provider.Kind}\t{provider.BaseUrl}");
    }
});

app.Add("providers use", async (string providerId, CancellationToken cancellationToken) =>
{
    await ConfigFileUpdater.SetRootStringPropertyAsync("defaultProvider", providerId, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Default provider set to {providerId}.");
});

app.Add("models list", (string providerId) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));

    if (provider is null)
    {
        throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

    foreach (ModelOptions model in provider.Models)
    {
        Console.WriteLine($"{model.Id}\t{model.ProviderModelId}");
    }
});

app.Add("models use", async (string modelId, CancellationToken cancellationToken) =>
{
    await ConfigFileUpdater.SetRootStringPropertyAsync("defaultModel", modelId, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Default model set to {modelId}.");
});

app.Add("mcp list", () =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    foreach (McpServerOptions server in options.McpServers)
    {
        Console.WriteLine($"{server.Id}\t{server.Command}\t{server.Enabled}");
    }
});

app.Add("mcp tools", async (CancellationToken cancellationToken) =>
{
    McpToolProvider provider = host.Services.GetRequiredService<McpToolProvider>();
    IReadOnlyList<ITool> tools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
    foreach (ITool tool in tools)
    {
        Console.WriteLine($"{tool.Name}\t{tool.Description}");
    }
});

app.Add("credentials set", async (string targetName, string secret, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.SetSecretAsync(targetName, secret, cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Credential stored.");
});

app.Add("credentials get", async (string targetName, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    string? secret = await store.GetSecretAsync(targetName, cancellationToken).ConfigureAwait(false);
    if (secret is null)
    {
        Environment.ExitCode = 1;
        Console.WriteLine("Credential not found.");
        return;
    }

    Console.WriteLine(secret);
});

app.Add("credentials delete", async (string targetName, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.DeleteSecretAsync(targetName, cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Credential deleted.");
});

await app.RunAsync(args).ConfigureAwait(false);

internal static class ConfigFileUpdater
{
    public static async ValueTask SetRootStringPropertyAsync(
        string propertyName,
        string value,
        CancellationToken cancellationToken)
    {
        string directory = WinHarnessConfiguration.GetConfigurationDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");

        JsonDocument? document = File.Exists(path)
            ? JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
            : null;

        ArrayBufferWriter<byte> buffer = new();
        try
        {
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                bool wroteProperty = false;

                if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (property.NameEquals(propertyName))
                        {
                            writer.WriteString(propertyName, value);
                            wroteProperty = true;
                        }
                        else
                        {
                            writer.WritePropertyName(property.Name);
                            property.Value.WriteTo(writer);
                        }
                    }
                }

                if (!wroteProperty)
                {
                    writer.WriteString(propertyName, value);
                }

                writer.WriteEndObject();
            }
        }
        finally
        {
            document?.Dispose();
        }

        await File.WriteAllBytesAsync(path, buffer.WrittenMemory.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
