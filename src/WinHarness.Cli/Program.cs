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
using WinHarness.Cli.Chat;
using WinHarness.Cli.Configuration;
using WinHarness.Cli.Rendering;
using WinHarness.Cli.Tui;
using WinHarness.Configuration;
using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Mcp;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Sessions;
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

app.Add("chat", async (
    string? prompt = null,
    string? providerId = null,
    string? modelId = null,
    bool? renderMarkdown = null,
    bool tui = false,
    bool noSession = false,
    bool continueSession = false,
    bool resume = false,
    string? session = null,
    string? name = null,
    CancellationToken cancellationToken = default) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    string resolvedProviderId = providerId ?? options.DefaultProvider;
    string resolvedModelId = modelId ?? options.DefaultModel;
    bool effectiveRenderMarkdown = renderMarkdown ?? tui;

    if (string.IsNullOrWhiteSpace(resolvedProviderId) || string.IsNullOrWhiteSpace(resolvedModelId))
    {
        throw new InvalidOperationException("Configure defaultProvider/defaultModel or pass --provider-id and --model-id.");
    }

    ChatSessionBootstrapRequest bootstrapRequest = new(
        IsOneShot: !string.IsNullOrWhiteSpace(prompt),
        NoSession: noSession,
        ContinueSession: continueSession,
        Resume: resume,
        Session: session,
        Name: name);

    if (string.IsNullOrWhiteSpace(prompt))
    {
        if (tui)
        {
            await ChatTuiApp.RunAsync(
                    host.Services,
                    resolvedProviderId,
                    resolvedModelId,
                    effectiveRenderMarkdown,
                    bootstrapRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await ChatRepl.RunAsync(
                host.Services,
                resolvedProviderId,
                resolvedModelId,
                effectiveRenderMarkdown,
                bootstrapRequest,
                cancellationToken)
            .ConfigureAwait(false);
        return;
    }

    await ChatRepl.RunTurnAsync(
        host.Services,
        resolvedProviderId,
        resolvedModelId,
        prompt,
        effectiveRenderMarkdown,
        bootstrapRequest,
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

app.Add("models set-capabilities", async (
    string providerId,
    string modelId,
    bool streaming = true,
    bool toolCalling = true,
    bool vision = false,
    bool promptCaching = false,
    bool structuredOutput = false,
    bool reasoning = false,
    CancellationToken cancellationToken = default) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    ProviderCapabilities capabilities = new(streaming, toolCalling, vision, promptCaching, structuredOutput, reasoning);
    ModelOptions model = await configurator.SetModelCapabilitiesAsync(providerId, modelId, capabilities, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Model '{model.Id}' capabilities updated.");
});

app.Add("mcp list", () =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    foreach (McpServerOptions server in options.McpServers)
    {
        string target = string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase)
            ? server.Command
            : server.Endpoint ?? string.Empty;
        Console.WriteLine($"{server.Id}\t{server.Transport}\t{target}\t{server.Enabled}");
    }
});

app.Add("mcp add-stdio", async (
    string id,
    string command,
    string argumentsJson = "[]",
    string? workingDirectory = null,
    string environmentJson = "{}",
    bool enabled = true,
    int startupTimeoutSeconds = 30,
    CancellationToken cancellationToken = default) =>
{
    McpConfigurator configurator = host.Services.GetRequiredService<McpConfigurator>();
    IReadOnlyList<string> arguments = ParseStringArray(argumentsJson);
    IReadOnlyDictionary<string, string?> environment = ParseNullableStringDictionary(environmentJson);
    McpServerOptions server = await configurator.AddStdioServerAsync(
        id,
        command,
        arguments,
        workingDirectory,
        environment,
        enabled,
        startupTimeoutSeconds,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"MCP server '{server.Id}' saved (stdio, enabled={server.Enabled}).");
});

app.Add("mcp add-http", async (
    string id,
    string endpoint,
    string transport = "http",
    string headersJson = "{}",
    bool enabled = true,
    int startupTimeoutSeconds = 30,
    CancellationToken cancellationToken = default) =>
{
    McpConfigurator configurator = host.Services.GetRequiredService<McpConfigurator>();
    IReadOnlyDictionary<string, string> headers = ParseStringDictionary(headersJson);
    McpServerOptions server = await configurator.AddHttpServerAsync(
        id,
        transport,
        endpoint,
        headers,
        enabled,
        startupTimeoutSeconds,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"MCP server '{server.Id}' saved ({server.Transport}, enabled={server.Enabled}).");
});

app.Add("mcp remove", async (string id, CancellationToken cancellationToken) =>
{
    McpConfigurator configurator = host.Services.GetRequiredService<McpConfigurator>();
    await configurator.RemoveServerAsync(id, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"MCP server '{id}' removed.");
});

app.Add("mcp enable", async (string id, CancellationToken cancellationToken) =>
{
    McpConfigurator configurator = host.Services.GetRequiredService<McpConfigurator>();
    McpServerOptions server = await configurator.SetEnabledAsync(id, enabled: true, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"MCP server '{server.Id}' enabled.");
});

app.Add("mcp disable", async (string id, CancellationToken cancellationToken) =>
{
    McpConfigurator configurator = host.Services.GetRequiredService<McpConfigurator>();
    McpServerOptions server = await configurator.SetEnabledAsync(id, enabled: false, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"MCP server '{server.Id}' disabled.");
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

static IReadOnlyList<string> ParseStringArray(string json)
{
    using JsonDocument document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Array)
    {
        throw new InvalidOperationException("Expected a JSON string array.");
    }

    List<string> values = [];
    foreach (JsonElement element in document.RootElement.EnumerateArray())
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Expected a JSON string array.");
        }

        values.Add(element.GetString() ?? string.Empty);
    }

    return values;
}

static IReadOnlyDictionary<string, string> ParseStringDictionary(string json)
{
    using JsonDocument document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("Expected a JSON string object.");
    }

    Dictionary<string, string> values = new(StringComparer.Ordinal);
    foreach (JsonProperty property in document.RootElement.EnumerateObject())
    {
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Expected a JSON string object.");
        }

        values[property.Name] = property.Value.GetString() ?? string.Empty;
    }

    return values;
}

static IReadOnlyDictionary<string, string?> ParseNullableStringDictionary(string json)
{
    using JsonDocument document = JsonDocument.Parse(json);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidOperationException("Expected a JSON string object.");
    }

    Dictionary<string, string?> values = new(StringComparer.Ordinal);
    foreach (JsonProperty property in document.RootElement.EnumerateObject())
    {
        values[property.Name] = property.Value.ValueKind switch
        {
            JsonValueKind.String => property.Value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new InvalidOperationException("Expected a JSON string object.")
        };
    }

    return values;
}

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
        ChatSessionBootstrapRequest bootstrapRequest,
        CancellationToken cancellationToken)
    {
        WinHarnessOptions options = services.GetRequiredService<WinHarnessOptions>();
        ChatSession session = await CreateSessionAsync(
            services,
            providerId,
            modelId,
            renderMarkdown,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);

        WriteBanner(session);

        SlashCommandContext slashContext = new(
            services,
            services.GetRequiredService<SessionManagerFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            cancellationToken);

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
                SlashCommandResult result = await SlashCommandProcessor.ExecuteAsync(
                    options,
                    session,
                    input,
                    slashContext).ConfigureAwait(false);
                WriteSlashCommandMessages(result.Messages);
                if (result.ShouldExit)
                {
                    return;
                }

                continue;
            }

            await RunTurnAsync(
                services,
                session,
                input,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<ChatSession> CreateSessionAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        ChatSessionBootstrapRequest bootstrapRequest,
        CancellationToken cancellationToken)
    {
        SessionManagerFactory factory = services.GetRequiredService<SessionManagerFactory>();
        IContextFileLoader contextFileLoader = services.GetRequiredService<IContextFileLoader>();
        ISessionManager sessionManager = await ChatSessionBootstrap.ResolveAsync(
            factory,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);

        return ChatSessionBootstrap.CreateChatSession(
            sessionManager,
            contextFileLoader,
            providerId,
            modelId,
            renderMarkdown);
    }

    private static void WriteBanner(ChatSession session)
    {
        AnsiConsole.Write(new Rule("[bold]WinHarness chat[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[dim]provider[/] [bold]{Markup.Escape(session.ProviderId)}[/]  [dim]model[/] [bold]{Markup.Escape(session.ModelId)}[/]  [dim]markdown[/] {(session.RenderMarkdown ? "[green]on[/]" : "[grey]off[/]")}");

        if (session.IsEphemeral)
        {
            AnsiConsole.MarkupLine("[dim]session[/] [grey]ephemeral[/]");
        }
        else
        {
            string display = session.SessionManager.DisplayName ?? session.SessionManager.Header.Id;
            AnsiConsole.MarkupLine(
                $"[dim]session[/] [bold]{Markup.Escape(display)}[/]  [dim]file[/] {Markup.Escape(session.SessionManager.SessionFilePath ?? string.Empty)}");
        }

        string? contextLine = ContextBannerFormatter.Format(session.ProjectContext);
        if (contextLine is not null)
        {
            AnsiConsole.MarkupLine("[dim]" + Markup.Escape(contextLine) + "[/]");
        }

        AnsiConsole.MarkupLine("[dim]/help for commands · /exit to quit[/]");
        AnsiConsole.WriteLine();
    }

    private static void WriteSlashCommandMessages(IReadOnlyList<string> messages)
    {
        foreach (string message in messages)
        {
            AnsiConsole.MarkupLine("[dim]" + Markup.Escape(message) + "[/]");
        }
    }

    public static async ValueTask RunTurnAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        string prompt,
        bool renderMarkdown,
        ChatSessionBootstrapRequest bootstrapRequest,
        CancellationToken cancellationToken)
    {
        ChatSession session = await CreateSessionAsync(
            services,
            providerId,
            modelId,
            renderMarkdown,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);
        await RunTurnAsync(
            services,
            session,
            prompt,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RunTurnAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        CancellationToken cancellationToken)
    {
        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();
        StringBuilder? markdownBuffer = session.RenderMarkdown ? new StringBuilder() : null;
        Conversation runConversation = session.CreateRunConversation(prompt);

        // Tracks a transient, single-line tool status that overwrites itself in
        // place (via carriage return) so tool activity never adds scrollback lines.
        // Only real assistant output is allowed to advance to new lines.
        TransientStatusLine status = new();
        bool wroteAssistantLabel = false;

        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           new AgentRunRequest(
                               session.ProviderId,
                               session.ModelId,
                               runConversation,
                               session.WorkspaceRoot,
                               session.ProjectContext),
                           cancellationToken).ConfigureAwait(false))
        {
            if (agentEvent.Kind == AgentEventKind.ToolActivity)
            {
                status.Show(agentEvent.Message);
                continue;
            }

            if (agentEvent.Kind == AgentEventKind.Failed)
            {
                status.Clear();
                AnsiConsole.MarkupLine("[red]" + Markup.Escape(agentEvent.Message) + "[/]");
                continue;
            }

            if (agentEvent.Kind == AgentEventKind.Completed)
            {
                if (agentEvent.TurnArtifacts is not null)
                {
                    await session.AppendTurnAsync(agentEvent.TurnArtifacts, cancellationToken)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (agentEvent.Kind != AgentEventKind.AssistantDelta)
            {
                continue;
            }

            status.Clear();

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

        status.Clear();

        if (markdownBuffer is not null)
        {
            MarkdownConsoleRenderer.Write(markdownBuffer.ToString());
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Renders a single, self-overwriting status line for transient tool activity.
    /// The line is updated in place using a carriage return and is wiped before any
    /// real output is written, so tool messages never accumulate as scrollback.
    /// </summary>
    private sealed class TransientStatusLine
    {
        private int _renderedWidth;
        private bool _active;

        public void Show(string message)
        {
            if (Console.IsOutputRedirected)
            {
                return;
            }

            string text = Truncate(message);
            Console.Write('\r');
            AnsiConsole.Markup("[dim]" + Markup.Escape(text) + "[/]");

            if (text.Length < _renderedWidth)
            {
                Console.Write(new string(' ', _renderedWidth - text.Length));
            }

            _renderedWidth = text.Length;
            _active = true;
        }

        public void Clear()
        {
            if (!_active || Console.IsOutputRedirected)
            {
                _active = false;
                return;
            }

            Console.Write('\r');
            Console.Write(new string(' ', _renderedWidth));
            Console.Write('\r');
            _renderedWidth = 0;
            _active = false;
        }

        private static string Truncate(string message)
        {
            int max;
            try
            {
                max = Console.WindowWidth - 1;
            }
            catch (IOException)
            {
                return message;
            }

            if (max <= 0 || message.Length <= max)
            {
                return message;
            }

            return max <= 1 ? message[..max] : string.Concat(message.AsSpan(0, max - 1), "…");
        }
    }
}
