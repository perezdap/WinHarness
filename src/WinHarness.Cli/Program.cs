using System.Buffers;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using WinHarness;
using WinHarness.Cli.Rendering;
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

app.Add("chat", async (string? prompt = null, string? providerId = null, string? modelId = null, bool renderMarkdown = false, CancellationToken cancellationToken = default) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    string resolvedProviderId = providerId ?? options.DefaultProvider;
    string resolvedModelId = modelId ?? options.DefaultModel;

    if (string.IsNullOrWhiteSpace(resolvedProviderId) || string.IsNullOrWhiteSpace(resolvedModelId))
    {
        throw new InvalidOperationException("Configure defaultProvider/defaultModel or pass --provider-id and --model-id.");
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        await ChatRepl.RunAsync(host.Services, resolvedProviderId, resolvedModelId, renderMarkdown, cancellationToken)
            .ConfigureAwait(false);
        return;
    }

    await ChatRepl.RunTurnAsync(
        host.Services,
        resolvedProviderId,
        resolvedModelId,
        prompt,
        renderMarkdown,
        cancellationToken).ConfigureAwait(false);
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
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    if (!options.Providers.Any(provider => string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

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
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, options.DefaultProvider, StringComparison.OrdinalIgnoreCase));
    if (provider is null)
    {
        throw new InvalidOperationException("Configure a default provider before selecting a model.");
    }

    if (!provider.Models.Any(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
    {
        throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{provider.Id}'.");
    }

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
    CliValidation.ValidateCredentialTargetName(targetName);
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.SetSecretAsync(targetName, secret, cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Credential stored.");
});

app.Add("credentials get", async (string targetName, CancellationToken cancellationToken) =>
{
    CliValidation.ValidateCredentialTargetName(targetName);
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

app.Add("credentials list", async (CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    IReadOnlyList<string> targetNames = await store.ListTargetNamesAsync(cancellationToken).ConfigureAwait(false);
    foreach (string targetName in targetNames)
    {
        Console.WriteLine(targetName);
    }
});

app.Add("credentials delete", async (string targetName, CancellationToken cancellationToken) =>
{
    CliValidation.ValidateCredentialTargetName(targetName);
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

internal static class CliValidation
{
    public static void ValidateCredentialTargetName(string targetName)
    {
        if (!targetName.StartsWith("WinHarness:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Credential target names must start with 'WinHarness:'.");
        }
    }
}

internal static class ChatRepl
{
    public static async ValueTask RunAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        CancellationToken cancellationToken)
    {
        string currentProviderId = providerId;
        string currentModelId = modelId;

        AnsiConsole.MarkupLine("[dim]Enter /exit to quit, /provider <id> to switch provider, /model <id> to switch model.[/]");
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("winharness> ");
            string? input = Console.ReadLine();
            if (input is null || string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (input.StartsWith("/provider ", StringComparison.OrdinalIgnoreCase))
            {
                currentProviderId = input["/provider ".Length..].Trim();
                AnsiConsole.MarkupLine("[dim]Provider: " + Markup.Escape(currentProviderId) + "[/]");
                continue;
            }

            if (input.StartsWith("/model ", StringComparison.OrdinalIgnoreCase))
            {
                currentModelId = input["/model ".Length..].Trim();
                AnsiConsole.MarkupLine("[dim]Model: " + Markup.Escape(currentModelId) + "[/]");
                continue;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            await RunTurnAsync(
                services,
                currentProviderId,
                currentModelId,
                input,
                renderMarkdown,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async ValueTask RunTurnAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        string prompt,
        bool renderMarkdown,
        CancellationToken cancellationToken)
    {
        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();
        StringBuilder? markdownBuffer = renderMarkdown ? new StringBuilder() : null;

        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           new AgentRunRequest(providerId, modelId, prompt),
                           cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent.Kind != AgentEventKind.AssistantDelta)
            {
                continue;
            }

            if (markdownBuffer is null)
            {
                Console.Write(agentEvent.Message);
            }
            else
            {
                markdownBuffer.Append(agentEvent.Message);
            }
        }

        if (markdownBuffer is not null)
        {
            MarkdownConsoleRenderer.Write(markdownBuffer.ToString());
        }

        Console.WriteLine();
    }
}
