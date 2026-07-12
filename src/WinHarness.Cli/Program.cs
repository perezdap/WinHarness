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

const string Version = "0.3.0";

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
    IModelCapabilityResolver resolver = host.Services.GetRequiredService<IModelCapabilityResolver>();
    ProviderWizard wizard = new(configurator, store, catalog, resolver);
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

app.Add("models discover", async (
    string? baseUrl = null,
    string? apiKey = null,
    string? providerId = null,
    CancellationToken cancellationToken = default) =>
{
    if (string.IsNullOrWhiteSpace(providerId) == string.IsNullOrWhiteSpace(baseUrl))
    {
        throw new InvalidOperationException(
            "Pass exactly one of --provider-id (resolves base URL + auth + vendor headers from config) or --base-url (manual, with optional --api-key).");
    }

    IModelCatalog catalog = host.Services.GetRequiredService<IModelCatalog>();
    IModelCapabilityResolver resolver = host.Services.GetRequiredService<IModelCapabilityResolver>();

    string resolvedBaseUrl;
    string? resolvedApiKey;
    IReadOnlyDictionary<string, string>? extraHeaders = null;

    if (!string.IsNullOrWhiteSpace(providerId))
    {
        WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        resolvedBaseUrl = provider.BaseUrl
            ?? throw new InvalidOperationException($"Provider '{providerId}' has no base URL; run 'winharness login --provider {providerId}' or 'winharness config wizard'.");

        IProviderFactory factory = host.Services.GetRequiredService<IProviderFactory>();
        resolvedApiKey = await factory.CreateTokenSource(providerId)
            .GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(provider.Auth?.OAuthProvider, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            extraHeaders = GitHubCopilotOAuthFlow.CopilotHeaders;
        }
    }
    else
    {
        resolvedBaseUrl = baseUrl!;
        resolvedApiKey = apiKey;
    }

    IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
        resolvedBaseUrl, resolvedApiKey, cancellationToken, extraHeaders).ConfigureAwait(false);
    if (models.Count == 0)
    {
        Console.WriteLine("No models returned.");
        return;
    }

    foreach (CatalogModel model in models)
    {
        ProviderCapabilities caps = await resolver.ResolveAsync(model, cancellationToken).ConfigureAwait(false);
        List<string> enabled = [];
        if (caps.Streaming)        enabled.Add("streaming");
        if (caps.ToolCalling)      enabled.Add("toolCalling");
        if (caps.Vision)           enabled.Add("vision");
        if (caps.PromptCaching)    enabled.Add("promptCaching");
        if (caps.StructuredOutput) enabled.Add("structuredOutput");
        if (caps.Reasoning)        enabled.Add("reasoning");
        string capsText = enabled.Count == 0 ? "-" : string.Join(",", enabled);
        string prefix = model.OwnedBy is null ? model.Id : $"{model.Id}\t{model.OwnedBy}";
        Console.WriteLine($"{prefix}\t{capsText}");
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
    int? contextWindow = null,
    string[]? reasoningEfforts = null,
    string? reasoningEffort = null,
    bool setDefault = false,
    CancellationToken cancellationToken = default) =>
{
    ProviderConfigurator configurator = host.Services.GetRequiredService<ProviderConfigurator>();
    ProviderCapabilities capabilities = new(streaming, toolCalling, vision, promptCaching, structuredOutput, reasoning);
    System.Collections.Generic.List<string>? efforts = reasoningEfforts is null ? null : [.. reasoningEfforts];
    ModelOptions model = await configurator.AddModelAsync(providerId, id, providerModelId, capabilities, setDefault, contextWindow, efforts, reasoningEffort, cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Model '{model.Id}' ({model.ProviderModelId}) saved under '{providerId}'.");
});

app.Add("chat", async (
    string? prompt = null,
    string? providerId = null,
    string? modelId = null,
    bool? renderMarkdown = null,
    bool noSession = false,
    bool continueSession = false,
    bool resume = false,
    string? session = null,
    string? name = null,
    string? reasoningEffort = null,
    string[]? tools = null,
    string[]? excludeTools = null,
    bool noTools = false,
    string[]? files = null,
    string? output = null,
    bool approve = false,
    bool noApprove = false,
    string? template = null,
    string? templateArgs = null,
    bool verbose = false,
    CancellationToken cancellationToken = default) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    string resolvedProviderId = providerId ?? options.DefaultProvider;
    string resolvedModelId = modelId ?? options.DefaultModel;
    bool effectiveRenderMarkdown = renderMarkdown ?? true;

    // "model:effort" shorthand, e.g. --model-id gpt-primary:high.
    (resolvedModelId, string? shorthandEffort) = ChatRepl.SplitModelEffortShorthand(resolvedModelId);
    reasoningEffort ??= shorthandEffort;

    if (string.IsNullOrWhiteSpace(resolvedProviderId) || string.IsNullOrWhiteSpace(resolvedModelId))
    {
        throw new InvalidOperationException("Configure defaultProvider/defaultModel or pass --provider-id and --model-id.");
    }

    // --template expands a prompt template into the one-shot prompt (before
    // IsOneShot is computed, so --template alone routes to one-shot mode).
    if (!string.IsNullOrWhiteSpace(template))
    {
        bool templateTrust = approve || (!noApprove && new TrustStore().GetDecision(Environment.CurrentDirectory) == true) ||
            !TrustStore.HasProjectLocalResources(Environment.CurrentDirectory);
        prompt = ChatRepl.ExpandTemplateForOneShot(template, templateArgs, prompt, templateTrust);
    }

    ChatSessionBootstrapRequest bootstrapRequest = new(
        IsOneShot: !string.IsNullOrWhiteSpace(prompt),
        NoSession: noSession,
        ContinueSession: continueSession,
        Resume: resume,
        Session: session,
        Name: name,
        ApproveOverride: approve ? true : noApprove ? false : null);

    ToolFilter? toolFilter = ChatRepl.CreateToolFilter(tools, excludeTools, noTools);
    if (toolFilter is not null && !noTools)
    {
        await ChatRepl.WarnUnknownToolNamesAsync(host.Services, toolFilter, cancellationToken).ConfigureAwait(false);
    }

    // One-shot prompt assembly: piped stdin is prepended as a fenced block,
    // --files and inline @tokens expand to fenced attachments.
    if (!string.IsNullOrWhiteSpace(prompt))
    {
        prompt = await ChatRepl.AssembleOneShotPromptAsync(prompt, files, cancellationToken).ConfigureAwait(false);
    }
    else if (files is { Length: > 0 })
    {
        throw new InvalidOperationException("--files requires --prompt (interactive mode uses inline @path references).");
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("--output json requires --prompt (one-shot mode).");
        }

        await ChatRepl.RunAsync(
                host.Services,
                resolvedProviderId,
                resolvedModelId,
                effectiveRenderMarkdown,
                bootstrapRequest,
                reasoningEffort,
                toolFilter,
                cancellationToken,
                verbose)
            .ConfigureAwait(false);
        return;
    }

    if (string.Equals(output, "json", StringComparison.OrdinalIgnoreCase))
    {
        await ChatRepl.RunJsonTurnAsync(
            host.Services,
            resolvedProviderId,
            resolvedModelId,
            prompt,
            bootstrapRequest,
            reasoningEffort,
            toolFilter,
            cancellationToken).ConfigureAwait(false);
        return;
    }

    await ChatRepl.RunTurnAsync(
        host.Services,
        resolvedProviderId,
        resolvedModelId,
        prompt,
        effectiveRenderMarkdown,
        bootstrapRequest,
        reasoningEffort,
        toolFilter,
        cancellationToken,
        verbose).ConfigureAwait(false);
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
            JsonArgumentParser.MergeToolMetadata(
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
    ProviderOptions? targetProvider = options.Providers.FirstOrDefault(provider =>
        string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (targetProvider is null)
    {
        throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

    // Ensure the current default model is valid under the new provider.
    // If it isn't, pick the first available model from the target provider
    // or clear the default model so the CLI doesn't fail validation on next start.
    string? resolvedModel = options.DefaultModel;
    if (options.DefaultModel.Length > 0)
    {
        bool modelExists = targetProvider.Models.Any(model =>
            string.Equals(model.Id, options.DefaultModel, StringComparison.OrdinalIgnoreCase));
        if (!modelExists)
        {
            resolvedModel = targetProvider.Models.Count > 0
                ? targetProvider.Models[0].Id
                : string.Empty;
        }
    }

    if (resolvedModel != options.DefaultModel)
    {
        await ConfigFileUpdater.SetRootStringPropertiesAsync(
            new Dictionary<string, string>
            {
                ["defaultProvider"] = providerId,
                ["defaultModel"] = resolvedModel
            },
            cancellationToken).ConfigureAwait(false);

        if (resolvedModel.Length > 0)
        {
            Console.WriteLine($"Default provider set to {providerId}, model set to {resolvedModel}.");
        }
        else
        {
            Console.WriteLine($"Default provider set to {providerId}. No models configured for this provider; set one with 'models use'.");
        }
    }
    else
    {
        await ConfigFileUpdater.SetRootStringPropertyAsync("defaultProvider", providerId, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Default provider set to {providerId}.");
    }
});

app.Add("models list", (string? providerId = null, string? filter = null) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();
    Func<ModelOptions, bool> matches = CliValidation.CreateModelFilter(filter);

    // No argument: list every provider's models with a header per provider.
    if (string.IsNullOrWhiteSpace(providerId))
    {
        if (options.Providers.Count == 0)
        {
            Console.WriteLine("No providers configured.");
            return;
        }

        foreach (ProviderOptions provider in options.Providers)
        {
            List<ModelOptions> models = provider.Models.Where(matches).ToList();
            if (filter is not null && models.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"{provider.Id}\t{provider.BaseUrl}");
            if (models.Count == 0)
            {
                Console.WriteLine($"\t(no models configured)");
                continue;
            }

            foreach (ModelOptions model in models)
            {
                Console.WriteLine($"\t{model.Id}\t{model.ProviderModelId}");
            }
        }

        return;
    }

    // Single-provider filter: keep the original behavior (throw if missing).
    ProviderOptions? filtered = options.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));

    if (filtered is null)
    {
        throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
    }

    foreach (ModelOptions model in filtered.Models.Where(matches))
    {
        Console.WriteLine($"{model.Id}\t{model.ProviderModelId}");
    }
});

app.Add("models use", async (string modelId, string? providerId = null, CancellationToken cancellationToken = default) =>
{
    WinHarnessOptions options = host.Services.GetRequiredService<WinHarnessOptions>();

    // When --provider-id is given, switch both provider and model atomically.
    if (providerId is not null)
    {
        ProviderOptions? targetProvider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (targetProvider is null)
        {
            throw new InvalidOperationException($"Provider '{providerId}' is not configured.");
        }

        if (!targetProvider.Models.Any(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Model '{modelId}' is not configured for provider '{providerId}'.");
        }

        await ConfigFileUpdater.SetRootStringPropertiesAsync(
            new Dictionary<string, string>
            {
                ["defaultProvider"] = providerId,
                ["defaultModel"] = modelId
            },
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Default provider set to {providerId}, model set to {modelId}.");
        return;
    }

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
    IReadOnlyList<string> arguments = JsonArgumentParser.ParseStringArray(argumentsJson);
    IReadOnlyDictionary<string, string?> environment = JsonArgumentParser.ParseNullableStringDictionary(environmentJson);
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
    IReadOnlyDictionary<string, string> headers = JsonArgumentParser.ParseStringDictionary(headersJson);
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

app.Add("rpc", async (CancellationToken cancellationToken) =>
{
    WinHarness.Cli.Rpc.RpcHost rpcHost = new(host.Services);
    await rpcHost.RunAsync(cancellationToken).ConfigureAwait(false);
});

app.Add("login", async (string provider, string? enterpriseDomain = null, bool noDiscover = false, CancellationToken cancellationToken = default) =>
{
    if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
    {
        await LoginAnthropicAsync(host.Services, cancellationToken).ConfigureAwait(false);
        return;
    }

    if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
    {
        await LoginOpenAiCodexAsync(host.Services, cancellationToken).ConfigureAwait(false);
        return;
    }

    if (!string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"OAuth provider '{provider}' is not supported yet. Available: copilot, anthropic, openai.");
    }

    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    ConfigStore configStore = host.Services.GetRequiredService<ConfigStore>();
    using HttpClient http = new();
    GitHubCopilotOAuthFlow flow = new(http, enterpriseDomain ?? "github.com");

    CopilotDeviceCode device = await flow.StartDeviceFlowAsync(cancellationToken).ConfigureAwait(false);
    AnsiConsole.MarkupLine($"Visit [bold blue]{Markup.Escape(device.VerificationUri)}[/] and enter code [bold]{Markup.Escape(device.UserCode)}[/]");
    AnsiConsole.MarkupLine("[dim]Waiting for authorization… (Ctrl+C to cancel)[/]");

    string githubToken = await flow.PollForGitHubTokenAsync(device, cancellationToken).ConfigureAwait(false);
    OAuthTokenSet tokens = await flow.ExchangeForBearerAsync(githubToken, cancellationToken).ConfigureAwait(false);

    const string providerId = "copilot";
    await store.SetSecretAsync(
        OAuthCredentialNames.ForProvider(providerId),
        JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
        cancellationToken).ConfigureAwait(false);

    // Create or update the provider entry pointing at the token's proxy endpoint.
    WinHarnessOptions current = await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    ProviderOptions? existing = current.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (existing is null)
    {
        existing = new ProviderOptions { Id = providerId, Kind = "openai-compatible" };
        current.Providers.Add(existing);
    }

    existing.BaseUrl = tokens.BaseUrl ?? GitHubCopilotOAuthFlow.DefaultBaseUrl;
    existing.Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "copilot" };

    // Seed the model list from the live <baseUrl>/models endpoint (ADR-0005:
    // available model ids come from there). The freshly-exchanged bearer and
    // the required Copilot editor headers are attached to the request. On any
    // failure (network, 403, empty), fall back to a single gpt-4o entry so chat
    // still works and the user can re-run `models discover` later.
    bool discovered = false;
    if (!noDiscover)
    {
        try
        {
            IModelCatalog catalog = host.Services.GetRequiredService<IModelCatalog>();
            IReadOnlyList<CatalogModel> liveModels = await AnsiConsole.Status()
                .StartAsync("Discovering models…", async _ =>
                    await catalog.ListModelsAsync(
                        existing.BaseUrl,
                        tokens.AccessToken,
                        cancellationToken,
                        GitHubCopilotOAuthFlow.CopilotHeaders).ConfigureAwait(false))
                .ConfigureAwait(false);

            if (liveModels.Count > 0)
            {
                existing.Models.Clear();
                foreach (CatalogModel model in liveModels)
                {
                    existing.Models.Add(new ModelOptions
                    {
                        Id = model.Id,
                        ProviderModelId = model.Id,
                        Capabilities = new ProviderCapabilities(
                            Streaming: true,
                            ToolCalling: model.ToolCalling ?? true,
                            Vision: model.Vision ?? false,
                            PromptCaching: model.PromptCaching ?? false,
                            StructuredOutput: model.StructuredOutput ?? false,
                            Reasoning: model.Reasoning ?? false),
                        ContextWindow = model.ContextWindow
                    });
                }
                discovered = true;
                AnsiConsole.MarkupLine($"[green]✓[/] Discovered [bold]{liveModels.Count}[/] models from {Markup.Escape(existing.BaseUrl)}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]Model discovery failed:[/] {Markup.Escape(ex.Message)}");
        }
    }

    if (!discovered && existing.Models.Count == 0)
    {
        existing.Models.Add(new ModelOptions
        {
            Id = "gpt-4o",
            ProviderModelId = "gpt-4o",
            Capabilities = new ProviderCapabilities(
                Streaming: true, ToolCalling: true, Vision: true,
                PromptCaching: false, StructuredOutput: true, Reasoning: false),
            ContextWindow = 128_000
        });
        if (!noDiscover)
        {
            AnsiConsole.MarkupLine("[dim]Seeded a fallback gpt-4o model. Re-run discovery later with: winharness models discover --provider-id copilot[/]");
        }
    }

    await configStore.SaveAsync(current, cancellationToken).ConfigureAwait(false);
    AnsiConsole.MarkupLine($"[green]Logged in.[/] Provider '{providerId}' configured at {Markup.Escape(existing.BaseUrl)}.");
    if (!discovered)
    {
        AnsiConsole.MarkupLine("[dim]Discover models with: winharness models discover --provider-id copilot[/]");
    }
});

app.Add("login status", async (CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    IReadOnlyList<string> names = await store.ListTargetNamesAsync(cancellationToken).ConfigureAwait(false);
    bool any = false;
    foreach (string name in names.Where(static name => name.StartsWith("WinHarness:oauth:", StringComparison.Ordinal)))
    {
        any = true;
        string providerId = name["WinHarness:oauth:".Length..];
        string? secret = await store.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);
        string expiry = "unknown";
        if (secret is not null &&
            JsonSerializer.Deserialize(secret, WinHarnessJsonSerializerContext.Default.OAuthTokenSet) is { } tokens)
        {
            expiry = tokens.ExpiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "no expiry";
        }

        Console.WriteLine($"{providerId}\toauth\texpires {expiry}");
    }

    if (!any)
    {
        Console.WriteLine("No OAuth logins stored.");
    }
});

app.Add("logout", async (string provider, CancellationToken cancellationToken) =>
{
    ICredentialStore store = host.Services.GetRequiredService<ICredentialStore>();
    await store.DeleteSecretAsync(OAuthCredentialNames.ForProvider(provider), cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"OAuth tokens for '{provider}' deleted.");
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

app.Add("sessions prune", async (
    string olderThan,
    bool permanent = false,
    bool dryRun = false,
    bool allWorkspaces = false,
    CancellationToken cancellationToken = default) =>
{
    TimeSpan duration = ParseDuration(olderThan);
    DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(duration);

    SessionManagerFactory sessionFactory = host.Services.GetRequiredService<SessionManagerFactory>();
    IReadOnlyList<SessionSummary> summaries;

    if (allWorkspaces)
    {
        summaries = await sessionFactory.ListAllAsync(cancellationToken).ConfigureAwait(false);
    }
    else
    {
        summaries = await sessionFactory.ListAsync(Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
    }

    var itemsToPrune = summaries.Where(s => s.LastModified < cutoff).ToList();

    if (itemsToPrune.Count == 0)
    {
        Console.WriteLine("No sessions found matching the pruning criteria.");
        return;
    }

    Console.WriteLine($"Found {itemsToPrune.Count} session(s) older than {olderThan} (cutoff: {cutoff:g}):");
    foreach (var summary in itemsToPrune)
    {
        Console.WriteLine($"  - [{summary.SessionId}] Modified: {summary.LastModified.LocalDateTime:g} | Path: {summary.FilePath}");
    }

    if (dryRun)
    {
        Console.WriteLine("\nDry run mode enabled. No files were modified.");
        return;
    }

    Console.WriteLine();
    string modeLabel = permanent ? "permanently deleting" : "trashing";
    if (Console.IsInputRedirected)
    {
        Console.WriteLine($"Pruning sessions ({modeLabel})...");
    }
    else if (!AnsiConsole.Confirm($"Are you sure you want to prune these {itemsToPrune.Count} session(s) ({modeLabel})?"))
    {
        Console.WriteLine("Pruning cancelled.");
        return;
    }

    SessionDeletionService deletionService = new(sessionFactory);
    int prunedCount = 0;
    foreach (var summary in itemsToPrune)
    {
        try
        {
            var result = await deletionService.DeleteAsync(summary.FilePath, permanent, activeSessionPath: null, cancellationToken).ConfigureAwait(false);
            if (result.Status == SessionDeletionStatus.PermanentlyDeleted)
            {
                Console.WriteLine($"Deleted permanently: {summary.SessionId}");
            }
            else if (result.Status == SessionDeletionStatus.Trashed)
            {
                Console.WriteLine($"Moved to trash: {summary.SessionId} -> {result.FinalPath}");
            }
            prunedCount++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error pruning {summary.SessionId}: {ex.Message}");
        }
    }

    Console.WriteLine($"\nSuccessfully pruned {prunedCount} session(s).");

    static TimeSpan ParseDuration(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Duration value cannot be empty.");
        }

        value = value.Trim().ToLowerInvariant();
        char unit = value[^1];
        if (!char.IsLetter(unit))
        {
            throw new ArgumentException("Duration value must end with a time unit (e.g. 'd' for days, 'h' for hours).");
        }

        if (!double.TryParse(value[..^1], out double amount))
        {
            throw new ArgumentException($"Invalid numeric format for duration: '{value[..^1]}'.");
        }

        return unit switch
        {
            'd' => TimeSpan.FromDays(amount),
            'h' => TimeSpan.FromHours(amount),
            'm' => TimeSpan.FromMinutes(amount),
            's' => TimeSpan.FromSeconds(amount),
            _ => throw new ArgumentException($"Unknown duration time unit: '{unit}'. Supported units are 'd', 'h', 'm', 's'.")
        };
    }
});

await app.RunAsync(args).ConfigureAwait(false);


static async Task<AnthropicCallbackResult> WaitForAnthropicCallbackOrPasteAsync(
    AnthropicOAuthFlow flow,
    AnthropicPkceSession session,
    CancellationToken cancellationToken)
{
    using CancellationTokenSource raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Task<AnthropicCallbackResult> loopbackTask = flow.WaitForCallbackAsync(session, raceCts.Token).AsTask();
    Task<AnthropicCallbackResult> pasteTask = Task.Run(async () =>
    {
        while (!raceCts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key is ConsoleKey.Enter or ConsoleKey.P)
                {
                    string pasted = AnsiConsole.Ask<string>("Paste the authorization code or full redirect URL:");
                    return AnthropicOAuthFlow.ParseAuthorizationInput(pasted, session);
                }
            }

            try
            {
                await Task.Delay(100, raceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        raceCts.Token.ThrowIfCancellationRequested();
        throw new OperationCanceledException(raceCts.Token);
    }, raceCts.Token);

    Task completed = await Task.WhenAny(loopbackTask, pasteTask).ConfigureAwait(false);
    await raceCts.CancelAsync().ConfigureAwait(false);

    if (completed == loopbackTask)
    {
        return await loopbackTask.ConfigureAwait(false);
    }

    return await pasteTask.ConfigureAwait(false);
}


static async Task LoginOpenAiCodexAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    ICredentialStore store = services.GetRequiredService<ICredentialStore>();
    ConfigStore configStore = services.GetRequiredService<ConfigStore>();
    using HttpClient http = new();
    OpenAiCodexOAuthFlow flow = new(http);
    OpenAiCodexPkceSession session = flow.CreatePkceSession();

    AnsiConsole.MarkupLine($"Open [bold blue]{Markup.Escape(session.AuthorizeUrl)}[/] to authorize ChatGPT Plus/Pro (Codex).");
    AnsiConsole.MarkupLine("[dim]Waiting for browser callback… (Ctrl+C to cancel, or paste the redirect URL when prompted)[/]");

    OpenAiCodexCallbackResult callback;
    try
    {
        callback = await flow.WaitForCallbackAsync(session, cancellationToken).ConfigureAwait(false);
    }
    catch (InvalidOperationException bindError) when (bindError.Message.Contains("Could not bind OAuth callback", StringComparison.Ordinal))
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(bindError.Message)}[/]");
        string pasted = AnsiConsole.Ask<string>("Paste the authorization code or full redirect URL:");
        callback = OpenAiCodexOAuthFlow.ParseAuthorizationInput(pasted, session);
    }

    OAuthTokenSet tokens = await flow.ExchangeCodeAsync(session, callback, cancellationToken).ConfigureAwait(false);

    const string providerId = "openai";
    await store.SetSecretAsync(
        OAuthCredentialNames.ForProvider(providerId),
        JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
        cancellationToken).ConfigureAwait(false);

    WinHarnessOptions current = await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    ProviderOptions? existing = current.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (existing is null)
    {
        existing = new ProviderOptions { Id = providerId, Kind = "openai-codex-responses" };
        current.Providers.Add(existing);
    }

    existing.Kind = "openai-codex-responses";
    existing.BaseUrl = tokens.BaseUrl ?? OpenAiCodexOAuthFlow.DefaultBaseUrl;
    existing.Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "openai" };
    existing.CredentialName = null;

    if (existing.Models.Count == 0)
    {
        foreach (ModelSeed seed in OpenAiCodexOAuthFlow.DefaultModels)
        {
            existing.Models.Add(new ModelOptions
            {
                Id = seed.Id,
                ProviderModelId = seed.ProviderModelId,
                Capabilities = new ProviderCapabilities(
                    Streaming: true,
                    ToolCalling: true,
                    Vision: true,
                    PromptCaching: true,
                    StructuredOutput: false,
                    Reasoning: seed.Reasoning),
                ContextWindow = seed.ContextWindow,
                SupportedReasoningEfforts = seed.Reasoning ? ["minimal", "low", "medium", "high", "extra-high"] : null,
            });
        }
    }

    if (string.IsNullOrWhiteSpace(current.DefaultProvider))
    {
        current.DefaultProvider = providerId;
        current.DefaultModel = existing.Models[0].Id;
    }

    await configStore.SaveAsync(current, cancellationToken).ConfigureAwait(false);
    AnsiConsole.MarkupLine($"[green]Logged in.[/] Provider '{providerId}' configured at {Markup.Escape(existing.BaseUrl)}.");
    AnsiConsole.MarkupLine($"[dim]Seeded {existing.Models.Count} Codex models. Account: {Markup.Escape(tokens.AccountId ?? "?")}. Switch with: winharness models use {existing.Models[0].Id} --provider-id openai[/]");
}

static async Task LoginAnthropicAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    ICredentialStore store = services.GetRequiredService<ICredentialStore>();
    ConfigStore configStore = services.GetRequiredService<ConfigStore>();
    using HttpClient http = new();
    AnthropicOAuthFlow flow = new(http);
    AnthropicPkceSession session = flow.CreatePkceSession();

    AnsiConsole.MarkupLine($"Open [bold blue]{Markup.Escape(session.AuthorizeUrl)}[/] to authorize Claude Pro/Max.");
    AnsiConsole.MarkupLine("[dim]Waiting for browser callback… Press Enter to paste the redirect URL instead (Ctrl+C to cancel).[/]");

    AnthropicCallbackResult callback;
    try
    {
        callback = await WaitForAnthropicCallbackOrPasteAsync(flow, session, cancellationToken).ConfigureAwait(false);
    }
    catch (InvalidOperationException bindError) when (bindError.Message.Contains("Could not bind OAuth callback", StringComparison.Ordinal))
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(bindError.Message)}[/]");
        string pasted = AnsiConsole.Ask<string>("Paste the authorization code or full redirect URL:");
        callback = AnthropicOAuthFlow.ParseAuthorizationInput(pasted, session);
    }

    OAuthTokenSet tokens = await flow.ExchangeCodeAsync(session, callback, cancellationToken).ConfigureAwait(false);

    const string providerId = "anthropic";
    await store.SetSecretAsync(
        OAuthCredentialNames.ForProvider(providerId),
        JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
        cancellationToken).ConfigureAwait(false);

    WinHarnessOptions current = await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    ProviderOptions? existing = current.Providers.FirstOrDefault(candidate =>
        string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    if (existing is null)
    {
        existing = new ProviderOptions { Id = providerId, Kind = "anthropic-messages" };
        current.Providers.Add(existing);
    }

    existing.Kind = "anthropic-messages";
    existing.BaseUrl = tokens.BaseUrl ?? AnthropicOAuthFlow.DefaultBaseUrl;
    existing.Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "anthropic" };
    existing.CredentialName = null;

    if (existing.Models.Count == 0)
    {
        foreach (ModelSeed seed in AnthropicOAuthFlow.DefaultModels)
        {
            existing.Models.Add(new ModelOptions
            {
                Id = seed.Id,
                ProviderModelId = seed.ProviderModelId,
                Capabilities = new ProviderCapabilities(
                    Streaming: true,
                    ToolCalling: true,
                    Vision: false,
                    PromptCaching: true,
                    StructuredOutput: false,
                    Reasoning: seed.Reasoning),
                ContextWindow = seed.ContextWindow,
                SupportedReasoningEfforts = seed.Reasoning ? ["low", "medium", "high", "extra-high"] : null,
            });
        }
    }

    if (string.IsNullOrWhiteSpace(current.DefaultProvider))
    {
        current.DefaultProvider = providerId;
        current.DefaultModel = existing.Models[0].Id;
    }

    await configStore.SaveAsync(current, cancellationToken).ConfigureAwait(false);
    AnsiConsole.MarkupLine($"[green]Logged in.[/] Provider '{providerId}' configured at {Markup.Escape(existing.BaseUrl)}.");
    AnsiConsole.MarkupLine($"[dim]Seeded {existing.Models.Count} Claude models. Switch with: winharness models use {existing.Models[0].Id} --provider-id anthropic[/]");
}


internal static class ChatRepl
{
    /// <summary>
    /// One-shot turn with LF-delimited JSONL events on stdout. All human
    /// output goes to stderr; stdout carries only events. Exits the process
    /// nonzero on turn failure so scripts can branch without parsing.
    /// </summary>
    public static async ValueTask RunJsonTurnAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        string prompt,
        ChatSessionBootstrapRequest bootstrapRequest,
        string? reasoningEffort,
        ToolFilter? toolFilter,
        CancellationToken cancellationToken)
    {
        ChatSession session = await CreateSessionAsync(
            services,
            providerId,
            modelId,
            renderMarkdown: false,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);
        session.ReasoningEffort = reasoningEffort;
        session.ToolFilter = toolFilter;

        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();
        Conversation runConversation = session.CreateRunConversation(prompt);

        static void Emit(JsonChatEvent chatEvent) =>
            Console.Out.WriteLine(JsonSerializer.Serialize(chatEvent, JsonChatEventContext.Default.JsonChatEvent));

        Emit(JsonChatEvent.TurnStart(session.ProviderId, session.ModelId));
        bool failed = false;
        StringBuilder assistantText = new();

        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           new AgentRunRequest(
                               session.ProviderId,
                               session.ModelId,
                               runConversation,
                               session.WorkspaceRoot,
                               session.ProjectContext,
                               session.ReasoningEffort,
                               session.ToolFilter),
                           cancellationToken).ConfigureAwait(false))
        {
            switch (agentEvent.Kind)
            {
                case AgentEventKind.AssistantDelta:
                    assistantText.Append(agentEvent.Message);
                    Emit(JsonChatEvent.AssistantDelta(agentEvent.Message));
                    break;

                case AgentEventKind.ToolActivity when agentEvent.ToolActivity is { } info:
                    Emit(JsonChatEvent.Tool(info));
                    break;

                case AgentEventKind.Failed:
                    failed = true;
                    Emit(JsonChatEvent.FromError(agentEvent.Message));
                    break;

                case AgentEventKind.Completed:
                    if (agentEvent.TurnArtifacts is { } artifacts)
                    {
                        await session.AppendTurnAsync(artifacts, cancellationToken).ConfigureAwait(false);
                        ConversationMessage? assistant = artifacts.Messages
                            .LastOrDefault(static message => message.Role == ConversationRole.Assistant);
                        Emit(JsonChatEvent.AssistantMessage(assistant?.Text ?? assistantText.ToString()));
                        if (assistant?.Usage is { } usage)
                        {
                            Emit(JsonChatEvent.Usage(usage.InputTokens, usage.OutputTokens));
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        if (!failed)
        {
            Emit(JsonChatEvent.TurnEnd());
        }
        else
        {
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Expands --template for one-shot chat: named args come from
    /// --template-args ("key=value key2=value2"), and --prompt (when present)
    /// fills {{input}}. Returns the expanded prompt.
    /// </summary>
    public static string ExpandTemplateForOneShot(string templateName, string? templateArgs, string? prompt, bool trustProjectLocal)
    {
        IReadOnlyList<PromptTemplate> templates = PromptTemplateRegistry.Discover(Environment.CurrentDirectory, trustProjectLocal);
        PromptTemplate templateDefinition = templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Template '{templateName}' not found. Available: {(templates.Count == 0 ? "(none)" : string.Join(", ", templates.Select(static t => t.Name)))}.");

        (Dictionary<string, string> named, string extraFree) = PromptTemplateRegistry.ParseArguments(templateArgs ?? string.Empty);
        string freeText = string.Join(" ", new[] { extraFree, prompt ?? string.Empty }.Where(static part => part.Length > 0));
        (string expanded, IReadOnlyList<string> missing) = PromptTemplateRegistry.Expand(templateDefinition, named, freeText);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Template '{templateDefinition.Name}' has unfilled placeholders: {string.Join(", ", missing)}. Provide them via --template-args \"key=value\".");
        }

        return expanded;
    }

    /// <summary>
    /// Assembles the one-shot prompt: piped stdin (when redirected) is
    /// prepended as a fenced block, --files attachments and inline @tokens
    /// expand through the same code path as the interactive editor.
    /// </summary>
    /// <param name="stdin">
    /// Optional stdin reader for tests. When null and stdin is redirected,
    /// the real <see cref="Console.In"/> is read with a bounded probe (see
    /// <see cref="ReadRedirectedStdinOrNoneAsync"/>).
    /// </param>
    /// <param name="isInputRedirected">
    /// Optional override for <see cref="Console.IsInputRedirected"/> (tests).
    /// </param>
    public static async ValueTask<string> AssembleOneShotPromptAsync(
        string prompt,
        string[]? files,
        CancellationToken cancellationToken,
        TextReader? stdin = null,
        bool? isInputRedirected = null)
    {
        bool redirected = isInputRedirected ?? Console.IsInputRedirected;
        if (redirected)
        {
            string? piped = await ReadRedirectedStdinOrNoneAsync(stdin, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(piped))
            {
                prompt = "```stdin" + Environment.NewLine + piped + Environment.NewLine + "```" +
                    Environment.NewLine + Environment.NewLine + prompt;
            }
        }

        if (files is { Length: > 0 })
        {
            foreach (string file in files)
            {
                string fullPath = Path.GetFullPath(file);
                if (!File.Exists(fullPath))
                {
                    throw new InvalidOperationException($"--files: '{file}' was not found.");
                }

                // Reuse the @token expansion so limits/formatting stay uniform.
                prompt += Environment.NewLine + "@" + file;
            }
        }

        (string expanded, IReadOnlyList<string> attached) =
            EditorInput.ExpandFileReferences(prompt, Environment.CurrentDirectory);
        if (attached.Count > 0)
        {
            Console.Error.WriteLine($"attached: {string.Join(", ", attached)}");
        }

        return expanded;
    }

    /// <summary>
    /// Reads redirected stdin to end, or returns null when no data arrives
    /// within a short probe window. Real pipes (<c>Get-Content file | winharness</c>)
    /// deliver bytes immediately, so the probe succeeds; a redirected-but-empty
    /// stdin (e.g. inherited by a test runner, never sending EOF) would otherwise
    /// block forever. The read is forced onto a threadpool thread because
    /// <see cref="Console.In"/> is a <c>SyncTextReader</c> whose
    /// <c>ReadToEndAsync</c> runs <c>ReadToEnd</c> synchronously on the calling
    /// thread — awaiting it directly never yields, so a plain
    /// <see cref="Task.WhenAny"/> race would never reach the timeout. The read
    /// task is abandoned on timeout rather than cancelled (cancelling a console
    /// stream read can corrupt <see cref="Console.In"/> for later use; one-shot
    /// mode never reads stdin again). An injected <paramref name="stdin"/>
    /// (tests) is trusted to complete.
    /// </summary>
    private static async ValueTask<string?> ReadRedirectedStdinOrNoneAsync(
        TextReader? stdin,
        CancellationToken cancellationToken)
    {
        if (stdin is not null)
        {
            return (await stdin.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
        }

        Task<string> readTask = Task.Run(
            () => Console.In.ReadToEndAsync(CancellationToken.None),
            CancellationToken.None);

        Task winner = await Task.WhenAny(
            readTask,
            Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)).ConfigureAwait(false);

        return winner == readTask
            ? (await readTask.ConfigureAwait(false)).Trim()
            : null;
    }

    private static readonly string[] KnownEfforts = ["off", "minimal", "low", "medium", "high"];

    /// <summary>
    /// Splits a "model:effort" shorthand (e.g. "gpt-primary:high") into model id
    /// and effort. Returns the input unchanged when the suffix is not a known
    /// effort level, so model ids containing colons keep working.
    /// </summary>
    public static (string ModelId, string? Effort) SplitModelEffortShorthand(string modelId)
    {
        int separator = modelId.LastIndexOf(':');
        if (separator <= 0 || separator == modelId.Length - 1)
        {
            return (modelId, null);
        }

        string suffix = modelId[(separator + 1)..].ToLowerInvariant();
        return KnownEfforts.Contains(suffix)
            ? (modelId[..separator], suffix)
            : (modelId, null);
    }

    /// <summary>
    /// Builds a per-run tool gating policy from the chat command flags, or null
    /// when no gating was requested.
    /// </summary>
    public static ToolFilter? CreateToolFilter(string[]? tools, string[]? excludeTools, bool noTools)
    {
        if (noTools)
        {
            return new ToolFilter(DisableAll: true);
        }

        IReadOnlyList<string>? allow = SplitToolNames(tools);
        IReadOnlyList<string>? exclude = SplitToolNames(excludeTools);
        if (allow is null && exclude is null)
        {
            return null;
        }

        return new ToolFilter(allow, exclude);
    }

    /// <summary>
    /// Warns (does not fail) when a gating flag names a tool that is not
    /// currently discoverable — MCP servers may be offline or disabled.
    /// </summary>
    public static async ValueTask WarnUnknownToolNamesAsync(
        IServiceProvider services,
        ToolFilter toolFilter,
        CancellationToken cancellationToken)
    {
        ToolRegistry registry = services.GetRequiredService<ToolRegistry>();
        IReadOnlyDictionary<string, ITool> known = await registry.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> knownNames = new(known.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (string name in (toolFilter.Allow ?? []).Concat(toolFilter.Exclude ?? []))
        {
            if (!knownNames.Contains(name))
            {
                AnsiConsole.MarkupLine($"[yellow]warning:[/] unknown tool name '{Markup.Escape(name)}' in tool filter.");
            }
        }
    }

    // Accepts repeated flags and comma-separated values: --tools a,b --tools c.
    private static IReadOnlyList<string>? SplitToolNames(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return null;
        }

        List<string> names = [.. values
            .SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];
        return names.Count == 0 ? null : names;
    }

    public static async ValueTask RunAsync(
        IServiceProvider services,
        string providerId,
        string modelId,
        bool renderMarkdown,
        ChatSessionBootstrapRequest bootstrapRequest,
        string? reasoningEffort,
        ToolFilter? toolFilter,
        CancellationToken cancellationToken,
        bool verbose = false)
    {
        WinHarnessOptions options = services.GetRequiredService<WinHarnessOptions>();
        ChatSession session = await CreateSessionAsync(
            services,
            providerId,
            modelId,
            renderMarkdown,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);

        session.ReasoningEffort = reasoningEffort;
        session.ToolFilter = toolFilter;
        WriteBanner(session);

        SlashCommandContext slashContext = new(
            services,
            services.GetRequiredService<SessionManagerFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            cancellationToken);

        // Idle input uses plain blocking Console.ReadLine so interactive
        // pickers (Spectre prompts in /models, /tree, trust) own the console
        // exclusively. Steering input during a turn is read via
        // Console.KeyAvailable polling inside RunTurnWithSteeringAsync — never
        // from a competing background reader.
        Queue<string> followUps = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            string? input;
            if (followUps.Count > 0)
            {
                input = followUps.Dequeue();
                AnsiConsole.MarkupLine("[dim]› (follow-up)[/] " + Markup.Escape(input));
            }
            else
            {
                AnsiConsole.Markup("[bold green]›[/] ");
                input = Console.ReadLine();
            }

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

                // Prompt templates expand to a regular prompt; fall through and
                // run the expanded text as this turn's input.
                if (result.ExpandedPrompt is { Length: > 0 } expanded)
                {
                    input = expanded;
                    AnsiConsole.MarkupLine("[dim]template expanded:[/]");
                    Console.WriteLine(expanded);
                }
                else
                {
                    continue;
                }
            }

            switch (EditorInput.Classify(input))
            {
                case EditorInputKind.MultiLineStart:
                {
                    string? block = ReadMultiLineBlock(input);
                    if (block is null)
                    {
                        return;
                    }

                    if (block.Length == 0)
                    {
                        continue;
                    }

                    input = block;
                    break;
                }

                case EditorInputKind.CommandLocal:
                case EditorInputKind.CommandToModel:
                {
                    bool sendToModel = EditorInput.Classify(input) == EditorInputKind.CommandToModel;
                    string? modelMessage = await RunEditorCommandAsync(
                        services,
                        EditorInput.StripCommandPrefix(input),
                        sendToModel,
                        cancellationToken).ConfigureAwait(false);
                    if (modelMessage is null)
                    {
                        continue;
                    }

                    input = modelMessage;
                    break;
                }
            }

            // Expand @file references before the prompt is sent and persisted.
            (input, IReadOnlyList<string> attached) = EditorInput.ExpandFileReferences(input, session.WorkspaceRoot);
            if (attached.Count > 0)
            {
                AnsiConsole.MarkupLine("[dim]attached: " + Markup.Escape(string.Join(", ", attached)) + "[/]");
            }

            // Visually separate the user's prompt from the agent's response so the
            // boundary is obvious in the scrollback.
            AnsiConsole.WriteLine();

            await RunTurnWithSteeringAsync(
                services,
                session,
                input,
                followUps,
                cancellationToken,
                verbose).ConfigureAwait(false);

            if (!session.IsEphemeral)
            {
                string footer = UsageFooter.Format(session, options, UsageFooter.FindLastTurnUsage(session));
                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(footer) + "[/]");
            }

            // Trailing blank line closes the turn before the next prompt.
            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Runs a turn on a background task while polling for typed input:
    /// plain lines queue steering (delivered between tool round-trips),
    /// "&gt;&gt; text" queues a follow-up turn, and /abort cancels the turn.
    /// Uses Console.KeyAvailable polling, never a background reader, so
    /// interactive pickers outside turns own the console exclusively. When
    /// input is redirected, in-turn steering is unavailable and the turn is
    /// simply awaited.
    /// </summary>
    private static async ValueTask RunTurnWithSteeringAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        Queue<string> followUps,
        CancellationToken cancellationToken,
        bool verbose)
    {
        using CancellationTokenSource turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task turn = RunTurnAsync(services, session, prompt, turnCts.Token, verbose).AsTask();

        if (Console.IsInputRedirected)
        {
            await AwaitTurnAsync(turn, cancellationToken).ConfigureAwait(false);
            return;
        }

        StringBuilder pending = new();
        while (!turn.IsCompleted)
        {
            string? line = TryReadLineNonBlocking(pending);
            if (line is null)
            {
                await Task.WhenAny(turn, Task.Delay(50, CancellationToken.None)).ConfigureAwait(false);
                continue;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (string.Equals(line, "/abort", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[yellow]aborting turn…[/]");
                await turnCts.CancelAsync().ConfigureAwait(false);

                // Restore unsent steering messages as follow-up input.
                foreach (string queued in session.Steering.DrainAll())
                {
                    followUps.Enqueue(queued);
                }

                break;
            }

            if (line.StartsWith(">>", StringComparison.Ordinal))
            {
                string followUp = line[2..].Trim();
                if (followUp.Length > 0)
                {
                    followUps.Enqueue(followUp);
                    AnsiConsole.MarkupLine("[dim]queued follow-up (runs after this turn)[/]");
                }

                continue;
            }

            session.Steering.Enqueue(line);
            AnsiConsole.MarkupLine("[dim]queued steering (delivered after current tool calls)[/]");
        }

        // A partial line typed as the turn ended becomes the next prompt seed.
        if (pending.Length > 0)
        {
            followUps.Enqueue(StripFollowUpPrefix(pending.ToString().Trim()));
        }

        await AwaitTurnAsync(turn, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AwaitTurnAsync(Task turn, CancellationToken cancellationToken)
    {
        try
        {
            await turn.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]turn aborted.[/]");
        }
    }

    /// <summary>
    /// Drains available keys without blocking. Returns a completed line when
    /// Enter arrives, null otherwise; partial input accumulates in the buffer.
    /// Backspace edits the buffer; other control keys are ignored.
    /// </summary>
    private static string? TryReadLineNonBlocking(StringBuilder pending)
    {
        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                string line = pending.ToString();
                pending.Clear();
                return line;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (pending.Length > 0)
                {
                    pending.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                pending.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }

        return null;
    }

    private static string StripFollowUpPrefix(string line) =>
        line.StartsWith(">>", StringComparison.Ordinal) ? line[2..].Trim() : line;

    /// <summary>
    /// Reads lines until a closing \"\"\" marker. Text after the opening marker
    /// on the same line becomes the first line of the block. Returns null on
    /// EOF, the joined block otherwise.
    /// </summary>
    private static string? ReadMultiLineBlock(string openingLine)
    {
        List<string> lines = [];
        string remainder = openingLine[EditorInput.MultiLineMarker.Length..].Trim();
        if (remainder.Length > 0)
        {
            lines.Add(remainder);
        }

        AnsiConsole.MarkupLine("[dim]multi-line input — end with \"\"\" on its own line[/]");
        while (true)
        {
            string? line = Console.ReadLine();
            if (line is null)
            {
                return null;
            }

            if (line.Trim() == EditorInput.MultiLineMarker)
            {
                return string.Join(Environment.NewLine, lines).Trim();
            }

            lines.Add(line);
        }
    }

    /// <summary>
    /// Runs a `!`/`!!` editor command through the captured executor via cmd.exe
    /// (or /bin/sh off Windows). Prints the output; returns a formatted user
    /// message when the output should go to the model, null otherwise.
    /// </summary>
    private static async ValueTask<string?> RunEditorCommandAsync(
        IServiceProvider services,
        string command,
        bool sendToModel,
        CancellationToken cancellationToken)
    {
        if (command.Length == 0)
        {
            return null;
        }

        ICommandExecutor executor = services.GetRequiredService<ICommandExecutor>();
        CommandRequest request = OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", ["/c", command], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(120))
            : new CommandRequest("/bin/sh", ["-c", command], Environment.CurrentDirectory, CommandExecutionMode.Captured, TimeSpan.FromSeconds(120));

        CommandResult result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.StandardOutput.Length > 0)
        {
            Console.WriteLine(result.StandardOutput.TrimEnd());
        }

        if (result.StandardError.Length > 0)
        {
            Console.Error.WriteLine(result.StandardError.TrimEnd());
        }

        if (result.ExitCode != 0)
        {
            AnsiConsole.MarkupLine($"[yellow]exit code {result.ExitCode}[/]");
        }

        return sendToModel
            ? EditorInput.FormatCommandOutputForModel(command, result.StandardOutput, result.StandardError, result.ExitCode)
            : null;
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

        bool trusted = ResolveProjectTrust(
            services,
            interactive: !bootstrapRequest.IsOneShot && !Console.IsInputRedirected,
            bootstrapRequest.ApproveOverride);

        return ChatSessionBootstrap.CreateChatSession(
            sessionManager,
            contextFileLoader,
            providerId,
            modelId,
            renderMarkdown,
            trusted);
    }

    /// <summary>
    /// Resolves whether project-local resources (.winharness SYSTEM.md,
    /// project skills) load for this run. Order: per-run --approve/--no-approve
    /// override, saved trust.json decision (workspace or ancestor), interactive
    /// prompt (persisting always/never), then defaultProjectTrust setting
    /// (ask/never =&gt; untrusted, always =&gt; trusted).
    /// </summary>
    private static bool ResolveProjectTrust(IServiceProvider services, bool interactive, bool? approveOverride)
    {
        string workspaceRoot = Environment.CurrentDirectory;
        if (approveOverride is { } forced)
        {
            return forced;
        }

        if (!TrustStore.HasProjectLocalResources(workspaceRoot))
        {
            return true;
        }

        TrustStore trustStore = new();
        if (trustStore.GetDecision(workspaceRoot) is { } saved)
        {
            if (!saved)
            {
                AnsiConsole.MarkupLine("[dim]project-local resources ignored (untrusted; use /trust or --approve)[/]");
            }

            return saved;
        }

        if (interactive)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]This folder contains project-local WinHarness resources[/] [dim]({Markup.Escape(workspaceRoot)})[/]");
            AnsiConsole.MarkupLine("[dim]Project SYSTEM.md and skills can steer the model. Trust this folder?[/]");
            string choice = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]always / once / never[/]")
                    .AddChoice("always").AddChoice("once").AddChoice("never")
                    .DefaultValue("once"));
            switch (choice.ToLowerInvariant())
            {
                case "always":
                    trustStore.SaveDecision(workspaceRoot, trusted: true);
                    return true;
                case "never":
                    trustStore.SaveDecision(workspaceRoot, trusted: false);
                    return false;
                default:
                    return true;
            }
        }

        WinHarnessOptions options = services.GetRequiredService<WinHarnessOptions>();
        bool trusted = string.Equals(options.DefaultProjectTrust, "always", StringComparison.OrdinalIgnoreCase);
        if (!trusted)
        {
            Console.Error.WriteLine("project-local resources ignored (no trust decision; pass --approve to load them)");
        }

        return trusted;
    }

    private static void WriteBanner(ChatSession session)
    {
        AnsiConsole.Write(new Rule("[bold]WinHarness chat[/]").LeftJustified());
        string effort = session.ReasoningEffort ?? "default";
        AnsiConsole.MarkupLine(
            $"[dim]provider[/] [bold]{Markup.Escape(session.ProviderId)}[/]  [dim]model[/] [bold]{Markup.Escape(session.ModelId)}[/]  [dim]effort[/] [bold]{Markup.Escape(effort)}[/]  [dim]markdown[/] {(session.RenderMarkdown ? "[green]on[/]" : "[grey]off[/]")}");

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

        if (session.ToolFilter is { } filter)
        {
            List<string> parts = [];
            if (filter.DisableAll)
            {
                parts.Add("all tools disabled");
            }
            else
            {
                if (filter.Allow is { Count: > 0 } allow)
                {
                    parts.Add($"allow: {string.Join(",", allow)}");
                }

                if (filter.Exclude is { Count: > 0 } exclude)
                {
                    parts.Add($"exclude: {string.Join(",", exclude)}");
                }
            }

            AnsiConsole.MarkupLine($"[dim]tools[/] [yellow]{Markup.Escape(string.Join("  ", parts))}[/]");
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
        string? reasoningEffort,
        ToolFilter? toolFilter,
        CancellationToken cancellationToken,
        bool verbose = false)
    {
        ChatSession session = await CreateSessionAsync(
            services,
            providerId,
            modelId,
            renderMarkdown,
            bootstrapRequest,
            cancellationToken).ConfigureAwait(false);
        session.ReasoningEffort = reasoningEffort;
        session.ToolFilter = toolFilter;
        await RunTurnAsync(
            services,
            session,
            prompt,
            cancellationToken,
            verbose).ConfigureAwait(false);
    }

    private static async ValueTask RunTurnAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        CancellationToken cancellationToken,
        bool verbose)
    {
        WinHarnessOptions options = services.GetRequiredService<WinHarnessOptions>();
        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();

        // Proactive: compact before the turn when the active branch is close to
        // the model's context window.
        string? notice = await AutoCompactionService.TryProactiveCompactAsync(
            options,
            session,
            runtime,
            cancellationToken).ConfigureAwait(false);
        if (notice is not null)
        {
            AnsiConsole.MarkupLine("[dim]" + Markup.Escape(notice) + "[/]");
        }

        string? failureMessage = await ExecuteTurnCoreAsync(services, session, prompt, cancellationToken, verbose)
            .ConfigureAwait(false);

        // Reactive: when the provider rejected the request for context overflow,
        // compact and retry the turn once. A second failure is surfaced as-is.
        if (failureMessage is not null && AutoCompactionService.IsContextOverflow(failureMessage))
        {
            notice = await AutoCompactionService.TryReactiveCompactAsync(
                options,
                session,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (notice is not null)
            {
                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(notice) + "[/]");
                await ExecuteTurnCoreAsync(services, session, prompt, cancellationToken, verbose).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs one turn and renders its events. Returns the failure message when
    /// the turn ended in <see cref="AgentEventKind.Failed"/>, null on success.
    /// </summary>
    private static async ValueTask<string?> ExecuteTurnCoreAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        CancellationToken cancellationToken,
        bool verbose = false)
    {
        IAgentRuntime runtime = services.GetRequiredService<IAgentRuntime>();
        Conversation runConversation = session.CreateRunConversation(prompt);

        bool interactive = !Console.IsOutputRedirected;

        // Buffers the full assistant text (for non-interactive rendering) and the
        // current contiguous text "segment" (the text emitted between two tool
        // calls). Behaviour depends on the markdown toggle:
        //   markdown ON  — the spinner stays up while a segment buffers, then the
        //                  whole segment is printed as formatted markdown. This is
        //                  robust regardless of length: nothing to erase, so long
        //                  answers that scroll still render correctly.
        //   markdown OFF — raw tokens stream live as they arrive so the user sees
        //                  incremental progress, terminated by a newline per segment.
        StringBuilder assistantBuffer = new();
        StringBuilder segmentBuffer = new();
        AssistantStreamWriter writer = new();
        await using ThinkingIndicator thinking = new();
        ToolBatchRenderer toolBatch = new(verbose);

        bool segmentActive = false;
        bool rawLabelWritten = false;
        bool plainLabelWritten = false;

        // Tracks whether the turn produced any assistant text or ended in a failure,
        // so an empty provider completion (stream closed with no content) can be
        // surfaced to the user instead of silently returning to the prompt.
        bool producedAssistantText = false;
        bool turnFailed = false;
        string? failureMessage = null;

        // Ends the current assistant text segment. In markdown mode the spinner is
        // stopped and the buffered segment is rendered as formatted markdown; in raw
        // mode the streamed line is simply terminated.
        async ValueTask FinalizeSegmentAsync()
        {
            if (!segmentActive)
            {
                return;
            }

            segmentActive = false;

            if (session.RenderMarkdown)
            {
                await thinking.StopAsync().ConfigureAwait(false);
                if (segmentBuffer.Length > 0)
                {
                    AnsiConsole.Markup("[bold blue]•[/] ");
                    MarkdownConsoleRenderer.Write(segmentBuffer.ToString());
                }
            }
            else if (writer.HasOutput)
            {
                Console.WriteLine();
            }

            segmentBuffer.Clear();
            rawLabelWritten = false;
            writer = new AssistantStreamWriter();
        }

        if (interactive)
        {
            thinking.Start();
        }

        try
        {
            await foreach (AgentEvent agentEvent in runtime.RunAsync(
                               new AgentRunRequest(
                                   session.ProviderId,
                                   session.ModelId,
                                   runConversation,
                                   session.WorkspaceRoot,
                                   session.ProjectContext,
                                   session.ReasoningEffort,
                                   session.ToolFilter,
                                   session.Steering),
                               cancellationToken).ConfigureAwait(false))
            {
                switch (agentEvent.Kind)
                {
                    case AgentEventKind.ToolActivity:
                        if (interactive)
                        {
                            await FinalizeSegmentAsync().ConfigureAwait(false);

                            if (agentEvent.ToolActivity is { } info)
                            {
                                if (verbose)
                                {
                                    await thinking.StopAsync().ConfigureAwait(false);
                                }

                                toolBatch.OnEvent(info);
                                if (!verbose)
                                {
                                    thinking.SetLabel(toolBatch.LiveLabel);
                                }
                            }
                            else
                            {
                                await thinking.StopAsync().ConfigureAwait(false);
                                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(agentEvent.Message) + "[/]");
                                thinking.SetLabel("thinking");
                            }

                            thinking.Start();
                        }

                        break;

                    case AgentEventKind.Failed:
                        turnFailed = true;
                        failureMessage = agentEvent.Message;
                        if (interactive)
                        {
                            await FinalizeSegmentAsync().ConfigureAwait(false);
                            await thinking.StopAsync().ConfigureAwait(false);
                            toolBatch.Settle();
                        }

                        AnsiConsole.MarkupLine("[red]" + Markup.Escape(agentEvent.Message) + "[/]");
                        break;

                    case AgentEventKind.Completed:
                        if (agentEvent.TurnArtifacts is not null)
                        {
                            await session.AppendTurnAsync(agentEvent.TurnArtifacts, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        break;

                    case AgentEventKind.AssistantDelta:
                        if (interactive && toolBatch.HasPendingBatch)
                        {
                            await thinking.StopAsync().ConfigureAwait(false);
                            toolBatch.Settle();
                            thinking.SetLabel("thinking");
                            thinking.Start();
                        }

                        assistantBuffer.Append(agentEvent.Message);
                        if (!string.IsNullOrEmpty(agentEvent.Message))
                        {
                            producedAssistantText = true;
                        }

                        if (interactive)
                        {
                            segmentActive = true;
                            segmentBuffer.Append(agentEvent.Message);

                            if (!session.RenderMarkdown)
                            {
                                // Raw streaming: drop the spinner on first token, then
                                // emit tokens as they arrive.
                                if (!rawLabelWritten)
                                {
                                    await thinking.StopAsync().ConfigureAwait(false);
                                    rawLabelWritten = true;
                                }

                                writer.Write(agentEvent.Message);
                            }
                        }
                        else if (!session.RenderMarkdown)
                        {
                            if (!plainLabelWritten)
                            {
                                AnsiConsole.Markup("[bold blue]•[/] ");
                                plainLabelWritten = true;
                            }

                            Console.Write(agentEvent.Message);
                        }

                        break;

                    default:
                        break;
                }
            }
        }
        finally
        {
            await thinking.StopAsync().ConfigureAwait(false);
        }

        if (interactive)
        {
            await FinalizeSegmentAsync().ConfigureAwait(false);
            toolBatch.Settle();

            // The provider can close the stream with no text and no failure (e.g. an
            // empty completion or a response that was entirely reasoning tokens). Without
            // a notice the turn looks like a silent no-op, so tell the user explicitly.
            if (!producedAssistantText && !turnFailed)
            {
                AnsiConsole.MarkupLine("[yellow]The model returned an empty response. Try resending, or switch models with /model.[/]");
            }

            return failureMessage;
        }

        if (session.RenderMarkdown)
        {
            if (assistantBuffer.Length > 0)
            {
                MarkdownConsoleRenderer.Write(assistantBuffer.ToString());
            }

            Console.WriteLine();
        }
        else if (plainLabelWritten)
        {
            Console.WriteLine();
        }

        if (!producedAssistantText && !turnFailed)
        {
            Console.Error.WriteLine("The model returned an empty response.");
        }

        return failureMessage;
    }
}
