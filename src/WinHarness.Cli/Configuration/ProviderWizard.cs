using Spectre.Console;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Providers;


namespace WinHarness.Cli.Configuration;

/// <summary>
/// Interactive, terminal-driven front end over <see cref="ProviderConfigurator"/>.
/// It only handles prompting and presentation; every mutation is delegated to the
/// configurator so the wizard and the non-interactive CLI commands stay in sync.
/// </summary>
internal sealed class ProviderWizard
{
    private readonly ProviderConfigurator _configurator;
    private readonly ConfigStore _store;
    private readonly IModelCatalog _modelCatalog;

    public ProviderWizard(ProviderConfigurator configurator, ConfigStore store, IModelCatalog modelCatalog)
    {
        _configurator = configurator;
        _store = store;
        _modelCatalog = modelCatalog;
    }

    /// <summary>
    /// Runs the full guided setup loop: add providers and models, then pick
    /// defaults. Safe to run repeatedly against an existing configuration.
    /// </summary>
    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        AnsiConsole.Write(new Rule("[bold]WinHarness setup[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[dim]Editing {Markup.Escape(_store.Path)}[/]");
        AnsiConsole.WriteLine();

        bool keepGoing = true;
        while (keepGoing && !cancellationToken.IsCancellationRequested)
        {
            await AddProviderInteractiveAsync(cancellationToken).ConfigureAwait(false);
            keepGoing = AnsiConsole.Confirm("Add another provider?", defaultValue: false);
        }

        await PromptForDefaultsAsync(cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]Configuration saved.[/]");
    }

    /// <summary>
    /// Prompts for a single OpenAI-compatible provider (base URL + optional API
    /// key) and at least one model, persisting as it goes.
    /// </summary>
    public async ValueTask AddProviderInteractiveAsync(CancellationToken cancellationToken)
    {
        string id = AnsiConsole.Prompt(
            new TextPrompt<string>("Provider [green]id[/] (e.g. [grey]openai-main[/]):")
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Provider id is required.")
                    : ValidationResult.Success()));

        string baseUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("Base [green]URL[/] ([grey]https://api.openai.com/v1[/]):")
                .Validate(static value => Uri.TryCreate(value, UriKind.Absolute, out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Enter an absolute URL.")));

        string? apiKey = null;
        if (AnsiConsole.Confirm("Does this endpoint need an [green]API key[/]?", defaultValue: true))
        {
            apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("API key:")
                    .Secret()
                    .Validate(static value => string.IsNullOrWhiteSpace(value)
                        ? ValidationResult.Error("API key cannot be empty.")
                        : ValidationResult.Success()));
        }

        ProviderOptions provider = await _configurator.AddProviderAsync(
            id,
            baseUrl,
            apiKey,
            makeDefault: false,
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]✓[/] Provider [bold]{Markup.Escape(provider.Id)}[/] saved.");

        // Offer to query the endpoint's /v1/models route so the user can pick
        // real model ids instead of typing them by hand.
        bool discovered = await TryDiscoverModelsAsync(provider.Id, baseUrl, apiKey, cancellationToken).ConfigureAwait(false);

        bool addModel = !discovered || AnsiConsole.Confirm($"Add another model to [bold]{Markup.Escape(provider.Id)}[/] manually?", defaultValue: false);
        while (addModel && !cancellationToken.IsCancellationRequested)
        {
            await AddModelInteractiveAsync(provider.Id, cancellationToken).ConfigureAwait(false);
            addModel = AnsiConsole.Confirm($"Add another model to [bold]{Markup.Escape(provider.Id)}[/]?", defaultValue: false);
        }
    }

    /// <summary>
    /// Queries the endpoint's model list and lets the user multi-select which
    /// discovered ids to persist. Returns true when at least one model was added
    /// this way. Falls back gracefully (returns false) when discovery is declined
    /// or fails, so the caller can offer manual entry instead.
    /// </summary>
    private async ValueTask<bool> TryDiscoverModelsAsync(
        string providerId,
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Confirm("Discover models from the endpoint ([grey]GET /v1/models[/])?", defaultValue: true))
        {
            return false;
        }

        IReadOnlyList<CatalogModel> models;
        try
        {
            models = await AnsiConsole.Status()
                .StartAsync("Querying /v1/models…", async _ =>
                    await _modelCatalog.ListModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[yellow]Discovery failed:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The endpoint returned no models.[/]");
            return false;
        }

        List<string> chosen = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title($"Select models to add to [bold]{Markup.Escape(providerId)}[/] [grey](space to toggle)[/]:")
                .PageSize(15)
                .MoreChoicesText("[grey](move up and down to reveal more models)[/]")
                .NotRequired()
                .AddChoices(models.Select(model => model.Id)));

        if (chosen.Count == 0)
        {
            return false;
        }

        bool? sharedVision = null;
        List<string>? sharedReasoning = null;
        if (chosen.Count == 1)
        {
            CatalogModel? m = models.FirstOrDefault(m => string.Equals(m.Id, chosen[0], StringComparison.Ordinal));
            sharedVision = m?.Vision;
            sharedReasoning = m?.SupportedReasoningEfforts;
        }

        ProviderCapabilities capabilities = PromptCapabilities(chosen.Count == 1 ? chosen[0] : "all selected models", sharedVision, sharedReasoning);
        bool applySharedCapabilities = chosen.Count == 1 ||
            AnsiConsole.Confirm("Apply those capabilities to all selected models?", defaultValue: true);

        foreach (string providerModelId in chosen)
        {
            CatalogModel? catalogModel = models.FirstOrDefault(m => string.Equals(m.Id, providerModelId, StringComparison.Ordinal));
            string alias = chosen.Count == 1
                ? AnsiConsole.Prompt(
                    new TextPrompt<string>($"Alias for [grey]{Markup.Escape(providerModelId)}[/]:")
                        .DefaultValue(DeriveAlias(providerModelId)))
                : DeriveAlias(providerModelId);

            ProviderCapabilities modelCapabilities = applySharedCapabilities ? capabilities : PromptCapabilities(providerModelId, catalogModel?.Vision, catalogModel?.SupportedReasoningEfforts);

            await _configurator.AddModelAsync(
                providerId,
                alias,
                providerModelId,
                modelCapabilities,
                makeDefault: false,
                catalogModel?.ContextWindow,
                catalogModel?.SupportedReasoningEfforts,
                cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine($"[green]✓[/] Model [bold]{Markup.Escape(alias)}[/] [dim]{Markup.Escape(providerModelId)}[/] saved.");
        }

        return true;
    }

    /// <summary>
    /// Derives a readable alias from a provider model id. The tag is preserved
    /// because it is semantically significant — for Ollama, <c>:cloud</c>
    /// distinguishes the paid hosted endpoint from a local model of the same
    /// name — so we only strip an <c>org/</c> namespace prefix. The remaining
    /// <c>name:tag</c> is kept verbatim (for example <c>qwen3.5:cloud</c> stays
    /// <c>qwen3.5:cloud</c>), which also keeps distinct tags from colliding.
    /// </summary>
    private static string DeriveAlias(string providerModelId)
    {
        string candidate = providerModelId;
        int slash = candidate.LastIndexOf('/');
        if (slash >= 0 && slash < candidate.Length - 1)
        {
            candidate = candidate[(slash + 1)..];
        }

        return candidate.Length == 0 ? providerModelId : candidate;
    }

    /// <summary>
    /// Prompts for a single model alias and its capabilities under a provider.
    /// </summary>
    public async ValueTask AddModelInteractiveAsync(string providerId, CancellationToken cancellationToken)
    {
        string modelId = AnsiConsole.Prompt(
            new TextPrompt<string>("Model [green]alias[/] (e.g. [grey]gpt-primary[/]):")
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Model alias is required.")
                    : ValidationResult.Success()));

        string providerModelId = AnsiConsole.Prompt(
            new TextPrompt<string>("Provider model id (e.g. [grey]gpt-4.1[/]):")
                .Validate(static value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Provider model id is required.")
                    : ValidationResult.Success()));

        ProviderCapabilities capabilities = PromptCapabilities(providerModelId, endpointVision: null, endpointReasoningEfforts: null);

        await _configurator.AddModelAsync(
            providerId,
            modelId,
            providerModelId,
            capabilities,
            makeDefault: false,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]✓[/] Model [bold]{Markup.Escape(modelId)}[/] saved.");
    }

    private static ProviderCapabilities InferCapabilities(string modelId, bool? endpointVision, List<string>? endpointReasoningEfforts)
    {
        string lower = modelId.ToLowerInvariant();

        // 1. Streaming: almost all modern chat models support streaming.
        bool streaming = true;

        // Check for common non-chat models
        bool isNonChat = lower.Contains("embedding") ||
                         lower.Contains("moderation") ||
                         lower.Contains("whisper") ||
                         lower.Contains("tts") ||
                         lower.Contains("dall-e") ||
                         lower.Contains("rerank");

        // 2. Tool calling: supported by default for most modern chat/instruct models.
        bool toolCalling = !isNonChat;

        // 3. Vision: DO NOT guess vision by common knowledge. Use endpointVision if available, otherwise false.
        bool vision = endpointVision ?? false;

        // 4. Prompt Caching: Claude 3/3.5, Gemini 1.5/2.0, DeepSeek
        bool promptCaching = lower.Contains("claude-3") ||
                             lower.Contains("gemini-1.5") ||
                             lower.Contains("gemini-2.0") ||
                             lower.Contains("deepseek");

        // 5. Structured Output: OpenAI gpt-4o/gpt-4-turbo, Gemini 1.5/2.0
        bool structuredOutput = lower.Contains("gpt-4o") ||
                               lower.Contains("gpt-4-turbo") ||
                               lower.Contains("gemini-1.5") ||
                               lower.Contains("gemini-2.0");

        // 6. Reasoning: DO NOT guess reasoning by common knowledge. Use endpointReasoningEfforts.
        bool reasoning = endpointReasoningEfforts is not null && endpointReasoningEfforts.Count > 0;

        if (isNonChat)
        {
            streaming = false;
            toolCalling = false;
            vision = false;
            promptCaching = false;
            structuredOutput = false;
            reasoning = false;
        }

        return new ProviderCapabilities(
            Streaming: streaming,
            ToolCalling: toolCalling,
            Vision: vision,
            PromptCaching: promptCaching,
            StructuredOutput: structuredOutput,
            Reasoning: reasoning);
    }

    private static ProviderCapabilities PromptCapabilities(string modelId, bool? endpointVision, List<string>? endpointReasoningEfforts)
    {
        ProviderCapabilities inferred = InferCapabilities(modelId, endpointVision, endpointReasoningEfforts);

        MultiSelectionPrompt<string> prompt = new MultiSelectionPrompt<string>()
            .Title($"Model capabilities for [bold]{Markup.Escape(modelId)}[/] [grey](space to toggle, enter to confirm)[/]:")
            .NotRequired()
            .InstructionsText("[grey](defaults are inferred from model id)[/]")
            .AddChoices("streaming", "toolCalling", "vision", "promptCaching", "structuredOutput", "reasoning");

        if (inferred.Streaming) prompt.Select("streaming");
        if (inferred.ToolCalling) prompt.Select("toolCalling");
        if (inferred.Vision) prompt.Select("vision");
        if (inferred.PromptCaching) prompt.Select("promptCaching");
        if (inferred.StructuredOutput) prompt.Select("structuredOutput");
        if (inferred.Reasoning) prompt.Select("reasoning");

        List<string> selected = AnsiConsole.Prompt(prompt);

        if (selected.Count == 0)
        {
            selected = ["streaming", "toolCalling"];
        }

        return new ProviderCapabilities(
            Streaming: selected.Contains("streaming"),
            ToolCalling: selected.Contains("toolCalling"),
            Vision: selected.Contains("vision"),
            PromptCaching: selected.Contains("promptCaching"),
            StructuredOutput: selected.Contains("structuredOutput"),
            Reasoning: selected.Contains("reasoning"));
    }

    private async ValueTask PromptForDefaultsAsync(CancellationToken cancellationToken)
    {
        WinHarnessOptions options = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (options.Providers.Count == 0)
        {
            return;
        }

        string providerId = options.Providers.Count == 1
            ? options.Providers[0].Id
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Default [green]provider[/]:")
                    .AddChoices(options.Providers.Select(provider => provider.Id)));

        ProviderOptions provider = options.Providers.First(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));

        string? modelId = null;
        if (provider.Models.Count == 1)
        {
            modelId = provider.Models[0].Id;
        }
        else if (provider.Models.Count > 1)
        {
            modelId = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Default [green]model[/]:")
                    .AddChoices(provider.Models.Select(model => model.Id)));
        }

        await _configurator.SetDefaultsAsync(providerId, modelId, cancellationToken).ConfigureAwait(false);
    }
}
