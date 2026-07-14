using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using WinHarness.Configuration;
using WinHarness.Infrastructure;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Serialization;

internal static class LoginCommand
{
    /// <summary>
    /// Sign in with a subscription OAuth provider.
    /// </summary>
    /// <param name="provider">OAuth provider: copilot, anthropic, openai (alias: codex).</param>
    /// <param name="enterpriseDomain">GitHub Enterprise domain for Copilot (default: github.com).</param>
    /// <param name="noDiscover">Skip model discovery after login.</param>
    public static async Task Run(
        [FromServices] IServiceProvider services,
        string provider,
        string? enterpriseDomain = null,
        bool noDiscover = false,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(provider, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            await LoginAnthropicAsync(services, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            await LoginOpenAiCodexAsync(services, cancellationToken, noDiscover).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(provider, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"OAuth provider '{provider}' is not supported yet. Available: copilot, anthropic, openai.");
        }

        ICredentialStore store = services.GetRequiredService<ICredentialStore>();
        ConfigStore configStore = services.GetRequiredService<ConfigStore>();
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
                IModelCatalog catalog = services.GetRequiredService<IModelCatalog>();
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
    }

    private static async Task LoginOpenAiCodexAsync(
        IServiceProvider services,
        CancellationToken cancellationToken,
        bool noDiscover)
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
        existing.Auth = new ProviderAuthOptions
        {
            Scheme = "oauth",
            OAuthProvider = OpenAiCodexOAuthFlow.ProviderId
        };
        existing.CredentialName = null;

        bool discovered = false;
        if (!noDiscover)
        {
            try
            {
                IReadOnlyList<CatalogModel> liveModels = await AnsiConsole.Status()
                    .StartAsync("Discovering Codex models…", async _ =>
                        await new OpenAiCodexModelCatalog().ListModelsAsync(
                            existing.BaseUrl,
                            tokens.AccessToken,
                            tokens.AccountId ?? throw new InvalidOperationException("OpenAI token response did not include an account id."),
                            cancellationToken).ConfigureAwait(false))
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
                                Vision: model.Vision ?? true,
                                PromptCaching: model.PromptCaching ?? true,
                                StructuredOutput: model.StructuredOutput ?? false,
                                Reasoning: model.Reasoning ?? false),
                            ContextWindow = model.ContextWindow ?? 272_000,
                            SupportedReasoningEfforts = model.SupportedReasoningEfforts
                        });
                    }

                    discovered = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[yellow]Codex model discovery failed:[/] {Markup.Escape(ex.Message)}");
            }
        }

        if (!discovered && existing.Models.Count == 0)
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
        string action = discovered ? "Discovered" : "Seeded";
        AnsiConsole.MarkupLine($"[dim]{action} {existing.Models.Count} Codex models. Account: {Markup.Escape(tokens.AccountId ?? "?")}. Switch with: winharness models use {existing.Models[0].Id} --provider-id openai[/]");
    }

    private static async Task LoginAnthropicAsync(IServiceProvider services, CancellationToken cancellationToken)
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

    private static async Task<AnthropicCallbackResult> WaitForAnthropicCallbackOrPasteAsync(
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
}
