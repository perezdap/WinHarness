using System.IO;
using System.Linq;
using Spectre.Console;
using WinHarness.Configuration;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

internal static class SlashCommandProcessor
{
    public static SlashCommandResult Execute(WinHarnessOptions options, ChatSession session, string input) =>
        ExecuteAsync(options, session, input, context: null).AsTask().GetAwaiter().GetResult();

    public static async ValueTask<SlashCommandResult> ExecuteAsync(
        WinHarnessOptions options,
        ChatSession session,
        string input,
        SlashCommandContext? context = null)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        string argument = parts.Length > 1 ? parts[1] : string.Empty;

        return command switch
        {
            "/exit" or "/quit" => SlashCommandResult.Exit(),
            "/help" => SlashCommandResult.Handled(CreateHelpLines()),
            "/session" => SlashCommandResult.Handled(CreateSessionLines(session)),
            "/name" => await SetSessionNameAsync(session, argument, context).ConfigureAwait(false),
            "/new" => await CreateNewSessionAsync(session, context).ConfigureAwait(false),
            "/resume" => await ResumeSessionAsync(session, context).ConfigureAwait(false),
            "/delete" => await DeleteSessionAsync(session, argument, context).ConfigureAwait(false),
            "/providers" => await ListProvidersAsync(options, session, context).ConfigureAwait(false),
            "/models" => await ListModelsAsync(options, session, argument, context).ConfigureAwait(false),
            "/provider" => await SwitchProviderAsync(options, session, argument, context).ConfigureAwait(false),
            "/model" => await SwitchModelAsync(options, session, argument, context).ConfigureAwait(false),
            "/skills" => await ListSkillsAsync(session, context).ConfigureAwait(false),
            "/skill" => await SelectSkillAsync(session, argument, context).ConfigureAwait(false),
            "/markdown" => ToggleMarkdown(session),
            "/clear" => Clear(session),
            "/tree" => await ExecuteTreeAsync(session, context).ConfigureAwait(false),
            "/fork" => await ExecuteForkAsync(session, context).ConfigureAwait(false),
            "/compact" => await ExecuteCompactAsync(session, argument, context).ConfigureAwait(false),
            _ => SlashCommandResult.Handled([$"Unknown command '{command}'. Try /help."])
        };
    }

    private static ValueTask<SlashCommandResult> ExecuteTreeAsync(ChatSession session, SlashCommandContext? context)
    {
        if (context is null)
        {
            return new(MissingContext("/tree"));
        }

        return SlashCommandAdvanced.TreeAsync(session);
    }

    private static async ValueTask<SlashCommandResult> ExecuteForkAsync(ChatSession session, SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/fork");
        }

        return await SlashCommandAdvanced.ForkAsync(session, context).ConfigureAwait(false);
    }

    private static async ValueTask<SlashCommandResult> ExecuteCompactAsync(
        ChatSession session,
        string instructions,
        SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/compact");
        }

        return await SlashCommandAdvanced.CompactAsync(session, instructions, context).ConfigureAwait(false);
    }

    private static SlashCommandResult MissingContext(string command) =>
        SlashCommandResult.Handled([$"'{command}' is only available in an active chat session."]);

    private static IReadOnlyList<string> CreateHelpLines()
    {
        return
        [
            "/help                 Show this help",
            "/session              Show session file, id, name, and status",
            "/name <name>          Set session display name (persisted sessions)",
            "/new                  Start a new persisted session file",
            "/resume               Pick a saved session to open",
            "/delete [id-or-path]  Delete a session file (trashed by default)",
            "/providers            List configured providers",
            "/models [provider]    List models for a provider",
            "/provider <id>        Switch active provider",
            "/model <id>           Switch active model",
            "/skills               List discovered skills",
            "/skill <name|off>     Select a skill for the session (or clear it)",
            "/markdown             Toggle markdown rendering",
            "/tree                 Navigate session branch",
            "/fork                 Copy active branch to a new session file",
            "/compact [text]       Summarize older context and keep recent messages",
            "/clear                Clear the in-memory conversation view",
            "/exit, /quit          Leave the session"
        ];
    }

    private static IReadOnlyList<string> CreateSessionLines(ChatSession session)
    {
        ISessionManager manager = session.SessionManager;
        return
        [
            $"persisted: {(session.IsEphemeral ? "no (in-memory)" : "yes")}",
            $"file: {manager.SessionFilePath ?? "(none)"}",
            $"id: {manager.Header.Id}",
            $"name: {manager.DisplayName ?? "(none)"}",
            $"leaf: {manager.LeafEntryId ?? "(none)"}",
            $"messages: {session.CountActiveBranchMessages()}",
            $"provider/model: {session.ProviderId}/{session.ModelId}"
        ];
    }

    private static async ValueTask<SlashCommandResult> SetSessionNameAsync(
        ChatSession session,
        string name,
        SlashCommandContext? context)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return SlashCommandResult.Handled(["Usage: /name <display name>"]);
        }

        if (session.IsEphemeral)
        {
            return SlashCommandResult.Handled(
                ["Session names require a persisted session. Use /new or start without --no-session."]);
        }

        CancellationToken cancellationToken = context?.CancellationToken ?? CancellationToken.None;
        await session.SessionManager.AppendSessionInfoAsync(name, cancellationToken).ConfigureAwait(false);
        return SlashCommandResult.Handled([$"Session name set to '{name}'."]);
    }

    private static async ValueTask<SlashCommandResult> CreateNewSessionAsync(
        ChatSession session,
        SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/new");
        }

        ISessionManager created = await context.SessionFactory.CreateAsync(
            session.WorkspaceRoot,
            context.CancellationToken).ConfigureAwait(false);
        session.ReplaceSessionManager(created);
        return SlashCommandResult.Handled(
        [
            "New session file created.",
            $"file: {created.SessionFilePath}"
        ]);
    }

    private static async ValueTask<SlashCommandResult> ResumeSessionAsync(
        ChatSession session,
        SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/resume");
        }

        ISessionManager? opened = await ChatSessionBootstrap.PickSessionAsync(
            context.SessionFactory,
            session.WorkspaceRoot,
            nameForNewSession: null,
            context.CancellationToken).ConfigureAwait(false);
        if (opened is null)
        {
            return SlashCommandResult.Handled(["No saved sessions for this workspace."]);
        }

        (string? providerId, string? modelId) = ChatSessionBootstrap.TryRestoreModelChange(opened);
        session.ReplaceSessionManager(opened);
        if (providerId is not null)
        {
            session.ProviderId = providerId;
        }

        if (modelId is not null)
        {
            session.ModelId = modelId;
        }

        return SlashCommandResult.Handled(
        [
            "Session resumed.",
            $"file: {opened.SessionFilePath}",
            $"name: {opened.DisplayName ?? "(none)"}"
        ]);
    }

    private static async ValueTask<SlashCommandResult> DeleteSessionAsync(
        ChatSession session,
        string argument,
        SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/delete");
        }

        SessionDeletionService service = new(context.SessionFactory);
        string? activeSessionPath = session.SessionManager.SessionFilePath;

        if (string.IsNullOrWhiteSpace(argument))
        {
            var summaries = await context.SessionFactory.ListAsync(session.WorkspaceRoot, context.CancellationToken).ConfigureAwait(false);
            if (summaries.Count == 0)
            {
                return SlashCommandResult.Handled(["No saved sessions for this workspace."]);
            }

            var deletableSummaries = summaries.Where(s => !string.Equals(Path.GetFullPath(s.FilePath), Path.GetFullPath(activeSessionPath ?? string.Empty), StringComparison.OrdinalIgnoreCase)).ToList();
            if (deletableSummaries.Count == 0)
            {
                return SlashCommandResult.Handled(["No other deletable sessions in this workspace (the only session is active)."]);
            }

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<SessionSummary>()
                    .Title("Select a session to delete (will be moved to trash)")
                    .PageSize(10)
                    .AddChoices(deletableSummaries)
                    .UseConverter(static s =>
                        $"{s.SessionId} · {s.DisplayName ?? s.FirstUserPreview ?? "(untitled)"}"));

            if (!AnsiConsole.Confirm($"Are you sure you want to delete session '{selected.SessionId}'?"))
            {
                return SlashCommandResult.Handled(["Deletion cancelled."]);
            }

            var result = await service.DeleteAsync(selected.FilePath, permanent: false, activeSessionPath, context.CancellationToken).ConfigureAwait(false);
            return SlashCommandResult.Handled([
                $"Session '{selected.SessionId}' moved to trash.",
                $"Trashed file: {result.FinalPath}"
            ]);
        }

        string targetPath = argument.Trim();
        if (!File.Exists(targetPath))
        {
            var summaries = await context.SessionFactory.ListAsync(session.WorkspaceRoot, context.CancellationToken).ConfigureAwait(false);
            var matched = summaries.FirstOrDefault(s => s.SessionId.EndsWith(targetPath, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
            {
                targetPath = matched.FilePath;
            }
            else
            {
                var allSummaries = await context.SessionFactory.ListAllAsync(context.CancellationToken).ConfigureAwait(false);
                matched = allSummaries.FirstOrDefault(s => s.SessionId.EndsWith(targetPath, StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                {
                    targetPath = matched.FilePath;
                }
                else
                {
                    return SlashCommandResult.Handled([$"Error: Could not find session with ID or path '{argument.Trim()}'."]);
                }
            }
        }

        try
        {
            var result = await service.DeleteAsync(targetPath, permanent: false, activeSessionPath, context.CancellationToken).ConfigureAwait(false);
            return SlashCommandResult.Handled([
                $"Session '{Path.GetFileNameWithoutExtension(targetPath)}' moved to trash.",
                $"Trashed file: {result.FinalPath}"
            ]);
        }
        catch (InvalidOperationException ex)
        {
            return SlashCommandResult.Handled([$"Error: {ex.Message}"]);
        }
    }

    private static IReadOnlyList<string> CreateSkillLines(ChatSession session)
    {
        if (session.Skills.Count == 0)
        {
            return ["No skills found. Add SKILL.md files under .winharness/skills, .agents/skills, or %APPDATA%/WinHarness/skills."];
        }

        List<string> lines = new(session.Skills.Count + 1);
        foreach (SkillDefinition skill in session.Skills)
        {
            string marker = ReferenceEquals(skill, session.SelectedSkill) ? "*" : " ";
            lines.Add($"{marker} {skill.Name} - {Summarize(skill.Description)}");
        }

        lines.Add("Use /skill <name> to activate, /skill off to clear.");
        return lines;
    }

    private static async ValueTask<SlashCommandResult> SelectSkillAsync(
        ChatSession session,
        string argument,
        SlashCommandContext? context)
    {
        if (argument.Length == 0)
        {
            return await ListSkillsAsync(session, context).ConfigureAwait(false);
        }

        if (string.Equals(argument, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "none", StringComparison.OrdinalIgnoreCase))
        {
            session.SelectedSkill = null;
            return SlashCommandResult.Handled(["Skill cleared."]);
        }

        SkillDefinition? skill = session.Skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, argument, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return SlashCommandResult.Handled([$"Skill '{argument}' not found. Try /skills."]);
        }

        session.SelectedSkill = skill;
        return SlashCommandResult.Handled([$"Skill '{skill.Name}' activated."]);
    }

    private static string Summarize(string description)
    {
        string single = description.ReplaceLineEndings(" ").Trim();
        return single.Length <= 80 ? single : single[..77] + "...";
    }

    /// <summary>
    /// Returns true when the console supports interactive prompts (stdin is not redirected).
    /// </summary>
    private static bool IsInteractive => !Console.IsInputRedirected;

    private static ValueTask<SlashCommandResult> ListProvidersAsync(
        WinHarnessOptions options,
        ChatSession session,
        SlashCommandContext? context)
    {
        if (options.Providers.Count == 0)
        {
            return new(SlashCommandResult.Handled(["No providers configured. Run 'winharness config wizard'."]));
        }

        if (!IsInteractive)
        {
            return new(SlashCommandResult.Handled(CreateProviderLines(options, session.ProviderId)));
        }

        string current = session.ProviderId;
        SelectionPrompt<string> prompt = new()
        {
            Title = "Select a provider (Esc to cancel)"
        };
        prompt.PageSize(10)
              .MoreChoicesText("[grey](move up and down to reveal more providers)[/]")
              .AddChoices(options.Providers.Select(p =>
                  $"{(string.Equals(p.Id, current, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {p.Id} {p.BaseUrl}"));

        string selected = AnsiConsole.Prompt(prompt);
        string providerId = ExtractFirstToken(selected);

        // If the user re-selected the current provider, no-op.
        if (string.Equals(providerId, current, StringComparison.OrdinalIgnoreCase))
        {
            return new(SlashCommandResult.Handled([$"Already on provider '{providerId}'."]));
        }

        return SwitchProviderAsync(options, session, providerId, context);
    }

    private static async ValueTask<SlashCommandResult> ListModelsAsync(
        WinHarnessOptions options,
        ChatSession session,
        string argument,
        SlashCommandContext? context)
    {
        string providerId = argument.Length > 0 ? argument : session.ProviderId;
        ProviderOptions? provider = FindProvider(options, providerId);
        if (provider is null)
        {
            return SlashCommandResult.Handled([$"Provider '{providerId}' is not configured."]);
        }

        if (provider.Models.Count == 0)
        {
            return SlashCommandResult.Handled([$"No models configured for '{provider.Id}'."]);
        }

        if (!IsInteractive)
        {
            return SlashCommandResult.Handled(CreateModelLines(options, provider.Id, session.ModelId));
        }

        string current = session.ModelId;
        SelectionPrompt<string> prompt = new()
        {
            Title = $"Select a model for {provider.Id} (Esc to cancel)"
        };
        prompt.PageSize(10)
              .MoreChoicesText("[grey](move up and down to reveal more models)[/]")
              .AddChoices(provider.Models.Select(m =>
                  $"{(string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} {m.Id} {m.ProviderModelId}"));

        string selected = AnsiConsole.Prompt(prompt);
        string modelId = ExtractFirstToken(selected);

        // If the user re-selected the current model, no-op.
        if (string.Equals(modelId, current, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(provider.Id, session.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return SlashCommandResult.Handled([$"Already on model '{modelId}'."]);
        }

        // Ensure the provider is active, then switch the model.
        if (!string.Equals(provider.Id, session.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            session.ProviderId = provider.Id;
            await AppendModelChangeIfPersistedAsync(session, context).ConfigureAwait(false);
        }

        return await SwitchModelAsync(options, session, modelId, context).ConfigureAwait(false);
    }

    private static ValueTask<SlashCommandResult> ListSkillsAsync(
        ChatSession session,
        SlashCommandContext? context)
    {
        _ = context;

        if (session.Skills.Count == 0)
        {
            return new(SlashCommandResult.Handled(
                ["No skills found. Add SKILL.md files under .winharness/skills, .agents/skills, or %APPDATA%/WinHarness/skills."]));
        }

        if (!IsInteractive)
        {
            return new(SlashCommandResult.Handled(CreateSkillLines(session)));
        }

        SkillDefinition? current = session.SelectedSkill;
        SelectionPrompt<string> prompt = new()
        {
            Title = "Select a skill (Esc to cancel)"
        };
        prompt.PageSize(10)
              .MoreChoicesText("[grey](move up and down to reveal more skills)[/]")
              .AddChoices(session.Skills.Select(s =>
                  $"{(ReferenceEquals(s, current) ? "*" : " ")} {s.Name} - {Summarize(s.Description)}"));

        string selected = AnsiConsole.Prompt(prompt);
        string skillName = ExtractFirstToken(selected);

        SkillDefinition? skill = session.Skills.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return new(SlashCommandResult.Handled([$"Skill '{skillName}' not found."]));
        }

        if (ReferenceEquals(skill, current))
        {
            return new(SlashCommandResult.Handled([$"Skill '{skill.Name}' is already active."]));
        }

        session.SelectedSkill = skill;
        return new(SlashCommandResult.Handled([$"Skill '{skill.Name}' activated."]));
    }

    /// <summary>
    /// Extracts the first whitespace-delimited token from a formatted choice line.
    /// </summary>
    private static string ExtractFirstToken(string choice)
    {
        // Choices are formatted as "[*] id extra..." — skip the marker and take the id.
        ReadOnlySpan<char> span = choice.AsSpan().TrimStart();
        if (span.Length > 0 && span[0] is '*' or ' ')
        {
            span = span[1..].TrimStart();
        }

        int space = span.IndexOf(' ');
        return space < 0 ? span.ToString() : span[..space].ToString();
    }

    private static IReadOnlyList<string> CreateProviderLines(WinHarnessOptions options, string currentProviderId)
    {
        if (options.Providers.Count == 0)
        {
            return ["No providers configured. Run 'winharness config wizard'."];
        }

        List<string> lines = new(options.Providers.Count);
        foreach (ProviderOptions provider in options.Providers)
        {
            string marker = string.Equals(provider.Id, currentProviderId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            lines.Add($"{marker} {provider.Id} {provider.BaseUrl}");
        }

        return lines;
    }

    private static IReadOnlyList<string> CreateModelLines(WinHarnessOptions options, string providerId, string currentModelId)
    {
        ProviderOptions? provider = FindProvider(options, providerId);
        if (provider is null)
        {
            return [$"Provider '{providerId}' is not configured."];
        }

        if (provider.Models.Count == 0)
        {
            return [$"No models configured for '{provider.Id}'."];
        }

        List<string> lines = new(provider.Models.Count);
        foreach (ModelOptions model in provider.Models)
        {
            string marker = string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            lines.Add($"{marker} {model.Id} {model.ProviderModelId}");
        }

        return lines;
    }

    private static async ValueTask<SlashCommandResult> SwitchProviderAsync(
        WinHarnessOptions options,
        ChatSession session,
        string providerId,
        SlashCommandContext? context)
    {
        if (providerId.Length == 0)
        {
            return await ListProvidersAsync(options, session, context).ConfigureAwait(false);
        }

        ProviderOptions? provider = FindProvider(options, providerId);
        if (provider is null)
        {
            return SlashCommandResult.Handled([$"Provider '{providerId}' is not configured."]);
        }

        session.ProviderId = provider.Id;
        if (!provider.Models.Any(model => string.Equals(model.Id, session.ModelId, StringComparison.OrdinalIgnoreCase)))
        {
            session.ModelId = provider.Models.Count > 0 ? provider.Models[0].Id : string.Empty;
        }

        await AppendModelChangeIfPersistedAsync(session, context).ConfigureAwait(false);
        return SlashCommandResult.Handled([$"Provider {session.ProviderId}, model {session.ModelId}."]);
    }

    private static async ValueTask<SlashCommandResult> SwitchModelAsync(
        WinHarnessOptions options,
        ChatSession session,
        string modelId,
        SlashCommandContext? context)
    {
        ProviderOptions? provider = FindProvider(options, session.ProviderId);
        if (provider is null)
        {
            return SlashCommandResult.Handled([$"Provider '{session.ProviderId}' is not configured."]);
        }

        if (modelId.Length == 0)
        {
            return await ListModelsAsync(options, session, argument: session.ProviderId, context).ConfigureAwait(false);
        }

        ModelOptions? model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return SlashCommandResult.Handled([$"Model '{modelId}' is not configured for '{session.ProviderId}'."]);
        }

        session.ModelId = model.Id;
        await AppendModelChangeIfPersistedAsync(session, context).ConfigureAwait(false);
        return SlashCommandResult.Handled([$"Model {session.ModelId}."]);
    }

    private static async ValueTask AppendModelChangeIfPersistedAsync(
        ChatSession session,
        SlashCommandContext? context)
    {
        if (!session.SessionManager.IsPersisted)
        {
            return;
        }

        CancellationToken cancellationToken = context?.CancellationToken ?? CancellationToken.None;
        await session.SessionManager.AppendModelChangeAsync(
            session.ProviderId,
            session.ModelId,
            cancellationToken).ConfigureAwait(false);
    }

    private static SlashCommandResult ToggleMarkdown(ChatSession session)
    {
        session.RenderMarkdown = !session.RenderMarkdown;
        return SlashCommandResult.Handled([$"Markdown rendering {(session.RenderMarkdown ? "on" : "off")}."]);
    }

    private static SlashCommandResult Clear(ChatSession session)
    {
        session.Conversation.Clear();
        return SlashCommandResult.Handled(["Conversation view cleared."]);
    }

    private static ProviderOptions? FindProvider(WinHarnessOptions options, string providerId)
    {
        return options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record SlashCommandResult(bool ShouldExit, IReadOnlyList<string> Messages)
{
    public static SlashCommandResult Handled(IReadOnlyList<string> messages) => new(false, messages);

    public static SlashCommandResult Exit() => new(true, []);
}