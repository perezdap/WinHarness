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

app.Add("models discover", async (string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default) =>
{
    IModelCatalog catalog = host.Services.GetRequiredService<IModelCatalog>();
    IModelCapabilityResolver resolver = host.Services.GetRequiredService<IModelCapabilityResolver>();
    IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
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

    ChatSessionBootstrapRequest bootstrapRequest = new(
        IsOneShot: !string.IsNullOrWhiteSpace(prompt),
        NoSession: noSession,
        ContinueSession: continueSession,
        Resume: resume,
        Session: session,
        Name: name);

    ToolFilter? toolFilter = ChatRepl.CreateToolFilter(tools, excludeTools, noTools);
    if (toolFilter is not null && !noTools)
    {
        await ChatRepl.WarnUnknownToolNamesAsync(host.Services, toolFilter, cancellationToken).ConfigureAwait(false);
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        await ChatRepl.RunAsync(
                host.Services,
                resolvedProviderId,
                resolvedModelId,
                effectiveRenderMarkdown,
                bootstrapRequest,
                reasoningEffort,
                toolFilter,
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
        reasoningEffort,
        toolFilter,
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
        await SetRootStringPropertiesAsync(
            new Dictionary<string, string> { [propertyName] = value },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically updates multiple root-level string properties in config.json,
    /// preserving all other properties.
    /// </summary>
    public static async ValueTask SetRootStringPropertiesAsync(
        IReadOnlyDictionary<string, string> updates,
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
                var written = new HashSet<string>(StringComparer.Ordinal);

                if (document is not null && document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        if (updates.TryGetValue(property.Name, out string? replacement))
                        {
                            writer.WriteString(property.Name, replacement);
                            written.Add(property.Name);
                        }
                        else
                        {
                            writer.WritePropertyName(property.Name);
                            property.Value.WriteTo(writer);
                        }
                    }
                }

                foreach ((string key, string value) in updates)
                {
                    if (!written.Contains(key))
                    {
                        writer.WriteString(key, value);
                    }
                }

                writer.WriteEndObject();
            }
        }
        finally
        {
            document?.Dispose();
        }

        await AtomicFile.WriteAllBytesAsync(path, buffer.WrittenMemory.ToArray(), cancellationToken).ConfigureAwait(false);
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
    /// <summary>
    /// Builds a case-insensitive wildcard predicate over model id and
    /// providerModelId. Supports '*' (any run) in the pattern; a pattern
    /// without '*' matches as a substring.
    /// </summary>
    public static Func<ModelOptions, bool> CreateModelFilter(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return static _ => true;
        }

        string regexPattern = pattern.Contains('*')
            ? "^" + string.Join(".*", pattern.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$"
            : System.Text.RegularExpressions.Regex.Escape(pattern);
        System.Text.RegularExpressions.Regex regex = new(
            regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        return model => regex.IsMatch(model.Id) || regex.IsMatch(model.ProviderModelId);
    }

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

        session.ReasoningEffort = reasoningEffort;
        session.ToolFilter = toolFilter;
        WriteBanner(session);

        SlashCommandContext slashContext = new(
            services,
            services.GetRequiredService<SessionManagerFactory>(),
            services.GetRequiredService<IAgentRuntime>(),
            cancellationToken);

        // Persistent background stdin reader so the user can type while a turn
        // is running (steering). Lines are consumed from the channel both by
        // the idle prompt loop and by the in-turn steering listener.
        System.Threading.Channels.Channel<string?> stdin =
            System.Threading.Channels.Channel.CreateUnbounded<string?>();
        _ = Task.Run(
            () =>
            {
                while (true)
                {
                    string? line = Console.ReadLine();
                    if (!stdin.Writer.TryWrite(line) || line is null)
                    {
                        return;
                    }
                }
            },
            CancellationToken.None);

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
                input = await stdin.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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

                continue;
            }

            // Visually separate the user's prompt from the agent's response so the
            // boundary is obvious in the scrollback.
            AnsiConsole.WriteLine();

            await RunTurnWithSteeringAsync(
                services,
                session,
                input,
                stdin.Reader,
                followUps,
                cancellationToken).ConfigureAwait(false);

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
    /// Runs a turn on a background task while listening for typed input:
    /// plain lines queue steering (delivered between tool round-trips),
    /// "&gt;&gt; text" queues a follow-up turn, and /abort cancels the turn.
    /// </summary>
    private static async ValueTask RunTurnWithSteeringAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        System.Threading.Channels.ChannelReader<string?> stdin,
        Queue<string> followUps,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task turn = RunTurnAsync(services, session, prompt, turnCts.Token).AsTask();

        while (!turn.IsCompleted)
        {
            Task<string?> readTask = stdin.ReadAsync(CancellationToken.None).AsTask();
            Task finished = await Task.WhenAny(turn, readTask).ConfigureAwait(false);
            if (finished == turn)
            {
                // Turn ended; re-queue an already-typed line as a follow-up so
                // it is not lost. It races the completion, so treat it as input
                // for the next prompt rather than steering.
                if (readTask.IsCompletedSuccessfully && readTask.Result is { Length: > 0 } tail)
                {
                    followUps.Enqueue(StripFollowUpPrefix(tail.Trim()));
                }

                break;
            }

            string? line = readTask.Result?.Trim();
            if (string.IsNullOrEmpty(line))
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

        try
        {
            await turn.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]turn aborted.[/]");
        }
    }

    private static string StripFollowUpPrefix(string line) =>
        line.StartsWith(">>", StringComparison.Ordinal) ? line[2..].Trim() : line;

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
        CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RunTurnAsync(
        IServiceProvider services,
        ChatSession session,
        string prompt,
        CancellationToken cancellationToken)
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

        string? failureMessage = await ExecuteTurnCoreAsync(services, session, prompt, cancellationToken)
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
                await ExecuteTurnCoreAsync(services, session, prompt, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
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

        // Renders a persistent, non-overwriting line for a tool activity event
        // so the user sees a running log of every tool call in the scrollback.
        void RenderToolActivityLine(ToolActivityInfo info)
        {
            string name = Markup.Escape(info.ToolName);

            switch (info.Phase)
            {
                case ToolActivityPhase.Started:
                    AnsiConsole.MarkupLine($"[dim]⠋[/] [bold]{name}[/]");
                    break;

                case ToolActivityPhase.Completed:
                {
                    string icon = info.Succeeded == false ? "[red]✗[/]" : "[green]✓[/]";
                    string duration = FormatDuration(info.Duration);
                    AnsiConsole.MarkupLine($"{icon} [bold]{name}[/] [dim]({duration})[/]");
                    break;
                }

                case ToolActivityPhase.Failed:
                {
                    string duration = FormatDuration(info.Duration);
                    string exc = info.ExceptionTypeName is null
                        ? ""
                        : $" [red]{Markup.Escape(info.ExceptionTypeName)}[/]";
                    AnsiConsole.MarkupLine($"[red]✗[/] [bold]{name}[/] [dim]({duration})[/]{exc}");
                    break;
                }
            }
        }

        static string FormatDuration(TimeSpan? duration)
        {
            if (duration is null)
            {
                return "";
            }

            double ms = duration.Value.TotalMilliseconds;
            if (ms >= 1000)
            {
                return duration.Value.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s";
            }

            return ms.ToString("F0", CultureInfo.InvariantCulture) + " ms";
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
                            // Close any in-progress assistant text segment, then
                            // render a persistent per-tool line that stays in the
                            // scrollback (like Claude Code / Codex do).
                            await FinalizeSegmentAsync().ConfigureAwait(false);
                            await thinking.StopAsync().ConfigureAwait(false);

                            if (agentEvent.ToolActivity is { } info)
                            {
                                RenderToolActivityLine(info);
                            }
                            else
                            {
                                // Fallback for events without structured payload.
                                AnsiConsole.MarkupLine("[dim]" + Markup.Escape(agentEvent.Message) + "[/]");
                            }

                            // Resume the spinner on a fresh line for the next
                            // operation (next tool, or assistant text).
                            thinking.SetLabel("thinking");
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

    /// <summary>
    /// Animated, self-overwriting "thinking" line shown while the agent is waiting
    /// for the model or running a tool. It renders a spinner, a label (default
    /// "thinking", or the latest tool activity), and elapsed seconds on a single
    /// line that is wiped before any real output is written. No-op when output is
    /// redirected so piped/captured output stays clean.
    /// </summary>
    private sealed class ThinkingIndicator : IAsyncDisposable
    {
        private static readonly char[] Frames =
            ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];

        private readonly bool _enabled = !Console.IsOutputRedirected;
        private readonly object _gate = new();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private CancellationTokenSource? _cts;
        private Task? _loop;
        private int _frame;
        private int _renderedWidth;
        private string _label = "thinking";

        public void Start()
        {
            if (!_enabled || _loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            _loop = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            lock (_gate)
                            {
                                Render();
                            }

                            await Task.Delay(120, token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                },
                token);
        }

        public void SetLabel(string label)
        {
            lock (_gate)
            {
                _label = Sanitize(label);
            }
        }

        public async ValueTask StopAsync()
        {
            CancellationTokenSource? cts = _cts;
            Task? loop = _loop;
            _cts = null;
            _loop = null;

            if (cts is null)
            {
                return;
            }

            await cts.CancelAsync().ConfigureAwait(false);
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            lock (_gate)
            {
                Erase();
            }

            cts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }

        private void Render()
        {
            char frame = Frames[_frame % Frames.Length];
            _frame++;

            string elapsed = _stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            string text = Truncate($"{frame} {_label} {elapsed}s");

            Console.Write('\r');
            AnsiConsole.Markup("[dim]" + Markup.Escape(text) + "[/]");

            if (text.Length < _renderedWidth)
            {
                Console.Write(new string(' ', _renderedWidth - text.Length));
            }

            _renderedWidth = text.Length;
        }

        private void Erase()
        {
            if (_renderedWidth == 0)
            {
                return;
            }

            Console.Write('\r');
            Console.Write(new string(' ', _renderedWidth));
            Console.Write('\r');
            _renderedWidth = 0;
        }

        private static string Sanitize(string message)
        {
            string single = message.Replace('\r', ' ').Replace('\n', ' ');
            return single.Length == 0 ? "thinking" : single;
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

    /// <summary>
    /// Streams raw assistant tokens to the console as they arrive while tracking the
    /// on-screen rows used, so the streamed block can be erased and re-rendered as
    /// markdown when the turn completes. Erase is only attempted when the output is
    /// interactive, the terminal size is known, and the block did not scroll.
    /// </summary>
    private sealed class AssistantStreamWriter
    {
        private readonly bool _interactive = !Console.IsOutputRedirected;
        private readonly int _width;
        private readonly int _height;
        private readonly bool _canMeasure;

        private bool _labelWritten;
        private int _column;
        private int _rows;

        public AssistantStreamWriter()
        {
            try
            {
                _width = Console.WindowWidth;
                _height = Console.WindowHeight;
                _canMeasure = _width > 0 && _height > 0;
            }
            catch (IOException)
            {
                _canMeasure = false;
            }
        }

        public bool HasOutput => _labelWritten;

        public void Write(string text)
        {
            if (!_labelWritten)
            {
                AnsiConsole.Markup("[bold blue]•[/] ");
                _labelWritten = true;
                _column = 2;
            }

            Console.Write(text);
            Track(text);
        }

        public bool TryEraseForReRender()
        {
            if (!_interactive || !_canMeasure || !_labelWritten)
            {
                return false;
            }

            // If the streamed block is taller than the window it has scrolled and the
            // saved relative position is no longer reliable; leave the raw text.
            if (_rows + 1 > _height)
            {
                return false;
            }

            if (_rows > 0)
            {
                Console.Write($"\x1b[{_rows.ToString(CultureInfo.InvariantCulture)}A");
            }

            Console.Write('\r');
            Console.Write("\x1b[0J");
            return true;
        }

        private void Track(string text)
        {
            foreach (char character in text)
            {
                if (character == '\n')
                {
                    _rows++;
                    _column = 0;
                }
                else if (character == '\r')
                {
                    _column = 0;
                }
                else
                {
                    _column++;
                    if (_canMeasure && _column >= _width)
                    {
                        _rows++;
                        _column = 0;
                    }
                }
            }
        }
    }
}
