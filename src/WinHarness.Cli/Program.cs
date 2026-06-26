using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinHarness;
using WinHarness.Cli.Configuration;
using WinHarness.Cli.Rendering;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Mcp;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Serialization;
using WinHarness.Tools;

const string Version = "0.1.0";

if (args is ["--version"] or ["-v"])
{
    Console.WriteLine(Version);
    return;
}

HostApplicationBuilder hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.Configuration.AddWinHarnessConfiguration();
hostBuilder.Logging.ClearProviders();
hostBuilder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
hostBuilder.Logging.SetMinimumLevel(LogLevel.Warning);
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

    string sample = JsonSerializer.Serialize(
        StarterConfiguration.Create(),
        WinHarnessJsonSerializerContext.Default.WinHarnessOptions);

    File.WriteAllText(path, sample);
    Console.WriteLine($"Wrote {path}");
});

app.Add("config wizard", async (CancellationToken cancellationToken) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    ConfigStore store = host.Services.GetRequiredService<ConfigStore>();
    IModelCatalog catalog = host.Services.GetRequiredService<IModelCatalog>();
    ProviderWizard wizard = new(configurator, store, catalog);
    await wizard.RunAsync(cancellationToken).ConfigureAwait(false);
    AnsiConsole.MarkupLine("[dim]Run [bold]winharness chat[/] to start a session.[/]");
});

app.Add("providers add", async (string id, string baseUrl, string? apiKey = null, bool setDefault = false, CancellationToken cancellationToken = default) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    ProviderOptions provider = await configurator.AddProviderAsync(id, baseUrl, apiKey, setDefault, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Provider '{provider.Id}' saved ({provider.BaseUrl}).");
    if (provider.CredentialName is not null)
    {
        Console.WriteLine($"API key stored as {provider.CredentialName}.");
    }
});

app.Add("providers remove", async (string id, CancellationToken cancellationToken) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    await configurator.RemoveProviderAsync(id, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Provider '{id}' removed.");
});

app.Add("models discover", async (string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default) =>
{
    IModelCatalog catalog = host.Services.GetRequiredService<IModelCatalog>();
    IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
    if (models.Count == 0)
    {
        Console.WriteLine("No models returned.");
        return;
    }

    foreach (CatalogModel model in models)
    {
        Console.WriteLine(model.OwnedBy is null ? model.Id : $"{model.Id}\t{model.OwnedBy}");
    }
});

app.Add("models add", async (
    string providerId,
    string id,
    string providerModelId,
    bool streaming = true,
    bool toolCalling = true,
    bool vision = false,
    bool promptCaching = false,
    bool structuredOutput = false,
    bool reasoning = false,
    bool setDefault = false,
    CancellationToken cancellationToken = default) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    ProviderCapabilities capabilities = new(streaming, toolCalling, vision, promptCaching, structuredOutput, reasoning);
    ModelOptions model = await configurator.AddModelAsync(providerId, id, providerModelId, capabilities, setDefault, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Model '{model.Id}' ({model.ProviderModelId}) saved under '{providerId}'.");
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
    Stopwatch stopwatch = Stopwatch.StartNew();
    IDiagnosticSink sink = host.Services.GetRequiredService<IDiagnosticSink>();
    ToolResult result;
    try
    {
        result = await tool.ExecuteAsync(
            new ToolInvocation(name, arguments.RootElement.Clone()),
            cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await sink.WriteAsync(
            new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                "tool",
                "tool.exception",
                name,
                new Dictionary<string, string>
                {
                    ["tool.name"] = name,
                    ["tool.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    ["exception.type"] = ex.GetType().FullName ?? ex.GetType().Name,
                    ["source"] = "cli"
                }),
            CancellationToken.None).ConfigureAwait(false);
        throw;
    }

    await sink.WriteAsync(
        new DiagnosticRecord(
            DateTimeOffset.UtcNow,
            "tool",
            result.Succeeded ? "tool.completed" : "tool.failed",
            name,
            MergeToolMetadata(
                new Dictionary<string, string>
                {
                    ["tool.name"] = name,
                    ["tool.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    ["tool.succeeded"] = result.Succeeded.ToString(CultureInfo.InvariantCulture),
                    ["tool.error_code"] = result.ErrorCode ?? string.Empty,
                    ["source"] = "cli"
                },
                result.Metadata)),
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

static Dictionary<string, string> MergeToolMetadata(
    Dictionary<string, string> properties,
    IReadOnlyDictionary<string, string>? metadata)
{
    if (metadata is null)
    {
        return properties;
    }

    foreach (KeyValuePair<string, string> pair in metadata)
    {
        properties[pair.Key] = pair.Value;
    }

    return properties;
}

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

internal static class StarterConfiguration
{
    public static WinHarnessOptions Create()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local-ollama",
            DefaultModel = "local-coder"
        };

        ProviderOptions provider = new()
        {
            Id = "local-ollama",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1"
        };

        provider.Models.Add(new ModelOptions
        {
            Id = "local-coder",
            ProviderModelId = "qwen2.5-coder:latest",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: false,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: false,
                Reasoning: false)
        });

        options.Providers.Add(provider);
        return options;
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
        WinHarnessOptions options = services.GetRequiredService<WinHarnessOptions>();
        string currentProviderId = providerId;
        string currentModelId = modelId;
        bool currentRenderMarkdown = renderMarkdown;
        Conversation conversation = new();

        WriteBanner(currentProviderId, currentModelId, currentRenderMarkdown);

        while (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.Markup("[bold green]›[/] ");
            string? input = Console.ReadLine();
            if (input is null)
            {
                return;
            }

            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }

            if (input.StartsWith('/'))
            {
                if (HandleSlashCommand(
                        options,
                        input,
                        ref currentProviderId,
                        ref currentModelId,
                        ref currentRenderMarkdown,
                        conversation,
                        out bool shouldExit))
                {
                    if (shouldExit)
                    {
                        return;
                    }

                    continue;
                }
            }

            await RunTurnAsync(
                services,
                currentProviderId,
                currentModelId,
                conversation,
                input,
                currentRenderMarkdown,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void WriteBanner(string providerId, string modelId, bool renderMarkdown)
    {
        AnsiConsole.Write(new Rule("[bold]WinHarness chat[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[dim]provider[/] [bold]{Markup.Escape(providerId)}[/]  [dim]model[/] [bold]{Markup.Escape(modelId)}[/]  [dim]markdown[/] {(renderMarkdown ? "[green]on[/]" : "[grey]off[/]")}");
        AnsiConsole.MarkupLine("[dim]/help for commands · /exit to quit[/]");
        AnsiConsole.WriteLine();
    }

    private static bool HandleSlashCommand(
        WinHarnessOptions options,
        string input,
        ref string currentProviderId,
        ref string currentModelId,
        ref bool currentRenderMarkdown,
        Conversation conversation,
        out bool shouldExit)
    {
        shouldExit = false;
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1] : string.Empty;

        switch (command)
        {
            case "/exit":
            case "/quit":
                shouldExit = true;
                return true;

            case "/help":
                WriteHelp();
                return true;

            case "/providers":
                WriteProviders(options, currentProviderId);
                return true;

            case "/models":
                WriteModels(options, argument.Length > 0 ? argument : currentProviderId, currentModelId);
                return true;

            case "/provider":
                SwitchProvider(options, argument, ref currentProviderId, ref currentModelId);
                return true;

            case "/model":
                SwitchModel(options, currentProviderId, argument, ref currentModelId);
                return true;

            case "/markdown":
                currentRenderMarkdown = !currentRenderMarkdown;
                AnsiConsole.MarkupLine($"[dim]Markdown rendering {(currentRenderMarkdown ? "[green]on[/]" : "[grey]off[/]")}.[/]");
                return true;

            case "/new":
            case "/clear":
                conversation.Clear();
                AnsiConsole.MarkupLine("[dim]Conversation cleared.[/]");
                return true;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'. Try /help.[/]");
                return true;
        }
    }

    private static void WriteHelp()
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Command")
            .AddColumn("Description");
        table.AddRow("/help", "Show this help");
        table.AddRow("/providers", "List configured providers");
        table.AddRow("/models [provider]", "List models for a provider");
        table.AddRow("/provider <id>", "Switch active provider");
        table.AddRow("/model <id>", "Switch active model");
        table.AddRow("/markdown", "Toggle markdown rendering");
        table.AddRow("/new, /clear", "Reset the conversation");
        table.AddRow("/exit, /quit", "Leave the session");
        AnsiConsole.Write(table);
    }

    private static void WriteProviders(WinHarnessOptions options, string currentProviderId)
    {
        if (options.Providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No providers configured. Run 'winharness config wizard'.[/]");
            return;
        }

        foreach (ProviderOptions provider in options.Providers)
        {
            bool active = string.Equals(provider.Id, currentProviderId, StringComparison.OrdinalIgnoreCase);
            string marker = active ? "[green]●[/] " : "  ";
            AnsiConsole.MarkupLine($"{marker}[bold]{Markup.Escape(provider.Id)}[/] [dim]{Markup.Escape(provider.BaseUrl ?? string.Empty)}[/]");
        }
    }

    private static void WriteModels(WinHarnessOptions options, string providerId, string currentModelId)
    {
        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            AnsiConsole.MarkupLine($"[red]Provider '{Markup.Escape(providerId)}' is not configured.[/]");
            return;
        }

        if (provider.Models.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No models configured for '{Markup.Escape(provider.Id)}'.[/]");
            return;
        }

        foreach (ModelOptions model in provider.Models)
        {
            bool active = string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase);
            string marker = active ? "[green]●[/] " : "  ";
            AnsiConsole.MarkupLine($"{marker}[bold]{Markup.Escape(model.Id)}[/] [dim]{Markup.Escape(model.ProviderModelId)}[/]");
        }
    }

    private static void SwitchProvider(
        WinHarnessOptions options,
        string providerId,
        ref string currentProviderId,
        ref string currentModelId)
    {
        if (providerId.Length == 0)
        {
            WriteProviders(options, currentProviderId);
            return;
        }

        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            AnsiConsole.MarkupLine($"[red]Provider '{Markup.Escape(providerId)}' is not configured.[/]");
            return;
        }

        currentProviderId = provider.Id;
        string activeModelId = currentModelId;
        if (!provider.Models.Any(model => string.Equals(model.Id, activeModelId, StringComparison.OrdinalIgnoreCase)))
        {
            currentModelId = provider.Models.Count > 0 ? provider.Models[0].Id : string.Empty;
        }

        AnsiConsole.MarkupLine($"[dim]Provider [bold]{Markup.Escape(currentProviderId)}[/], model [bold]{Markup.Escape(currentModelId)}[/].[/]");
    }

    private static void SwitchModel(
        WinHarnessOptions options,
        string currentProviderId,
        string modelId,
        ref string currentModelId)
    {
        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, currentProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            AnsiConsole.MarkupLine($"[red]Provider '{Markup.Escape(currentProviderId)}' is not configured.[/]");
            return;
        }

        if (modelId.Length == 0)
        {
            WriteModels(options, currentProviderId, currentModelId);
            return;
        }

        ModelOptions? model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            AnsiConsole.MarkupLine($"[red]Model '{Markup.Escape(modelId)}' is not configured for '{Markup.Escape(currentProviderId)}'.[/]");
            return;
        }

        currentModelId = model.Id;
        AnsiConsole.MarkupLine($"[dim]Model [bold]{Markup.Escape(currentModelId)}[/].[/]");
    }

    public static async ValueTask RunTurnAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        string prompt,
        bool renderMarkdown,
        CancellationToken cancellationToken)
    {
        await RunTurnAsync(
            services,
            providerId,
            modelId,
            new Conversation(),
            prompt,
            renderMarkdown,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RunTurnAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        Conversation conversation,
        string prompt,
        bool renderMarkdown,
        CancellationToken cancellationToken)
    {
        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();
        StringBuilder? markdownBuffer = renderMarkdown ? new StringBuilder() : null;
        bool wroteAssistantLabel = false;

        conversation.Add(new ConversationMessage(ConversationRole.User, prompt));

        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           new AgentRunRequest(providerId, modelId, conversation),
                           cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent.Kind == AgentEventKind.ToolActivity)
            {
                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(agentEvent.Message) + "[/]");
                continue;
            }

            if (agentEvent.Kind == AgentEventKind.Failed)
            {
                AnsiConsole.MarkupLine("[red]" + Markup.Escape(agentEvent.Message) + "[/]");
                continue;
            }

            if (agentEvent.Kind == AgentEventKind.Completed)
            {
                if (agentEvent.AssistantMessage is not null)
                {
                    conversation.Add(agentEvent.AssistantMessage);
                }

                continue;
            }

            if (agentEvent.Kind != AgentEventKind.AssistantDelta)
            {
                continue;
            }

            if (markdownBuffer is null)
            {
                if (!wroteAssistantLabel)
                {
                    AnsiConsole.Markup("[bold blue]•[/] ");
                    wroteAssistantLabel = true;
                }

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
