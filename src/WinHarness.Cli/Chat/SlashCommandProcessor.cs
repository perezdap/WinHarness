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
            "/clone" => await ExecuteCloneAsync(session, context).ConfigureAwait(false),
            "/export" => SlashCommandAdvanced.Export(session, argument),
            "/import" => await ExecuteImportAsync(session, argument, context).ConfigureAwait(false),
            "/effort" => SetEffort(session, argument),
            "/compact" => await ExecuteCompactAsync(session, argument, context).ConfigureAwait(false),
            "/usage" => ExecuteUsage(options, session),
            "/trust" => ExecuteTrust(session, argument),
            "/templates" => ListTemplates(session),
            "/t" => ExpandTemplate(session, argument),
            _ => SlashCommandResult.Handled([$"Unknown command '{command}'. Try /help."])
        };
    }

    private static SlashCommandResult ListTemplates(ChatSession session)
    {
        if (session.Templates.Count == 0)
        {
            return SlashCommandResult.Handled(
                ["No prompt templates found. Add .md files under .winharness/prompts, .agents/prompts, or the global prompts directory."]);
        }

        List<string> lines = ["Prompt templates:"];
        foreach (PromptTemplate template in session.Templates)
        {
            lines.Add($"  {template.Name}  {template.Description}");
        }

        lines.Add("Usage: /t <name> [key=value ...] [free text for {{input}}]");
        return SlashCommandResult.Handled(lines);
    }

    private static SlashCommandResult ExpandTemplate(ChatSession session, string argument)
    {
        string trimmed = argument.Trim();
        if (trimmed.Length == 0)
        {
            return ListTemplates(session);
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? trimmed : trimmed[..space];
        string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        PromptTemplate? template = session.Templates.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            return SlashCommandResult.Handled([$"Template '{name}' not found. /templates lists available templates."]);
        }

        (Dictionary<string, string> named, string freeText) = PromptTemplateRegistry.ParseArguments(rest);
        (string prompt, IReadOnlyList<string> missing) = PromptTemplateRegistry.Expand(template, named, freeText);
        if (missing.Count > 0)
        {
            return SlashCommandResult.Handled(
                [$"Template '{template.Name}' has unfilled placeholders: {string.Join(", ", missing)}. Provide them as key=value arguments."]);
        }

        return SlashCommandResult.Expanded(prompt);
    }

    private static SlashCommandResult ExecuteTrust(ChatSession session, string argument)
    {
        Infrastructure.Configuration.TrustStore trustStore = new();
        string normalized = argument.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "always":
            case "never":
                trustStore.SaveDecision(session.WorkspaceRoot, trusted: normalized == "always");
                return SlashCommandResult.Handled(
                    [$"Trust decision '{normalized}' saved for {session.WorkspaceRoot}. Restart chat to apply."]);
            case "":
                bool? saved = trustStore.GetDecision(session.WorkspaceRoot);
                string current = saved switch
                {
                    true => "always",
                    false => "never",
                    null => "undecided"
                };
                return SlashCommandResult.Handled(
                    [
                        $"Trust for {session.WorkspaceRoot}: {current} (this session: {(session.TrustProjectLocal ? "trusted" : "untrusted")}).",
                        "Usage: /trust always | /trust never"
                    ]);
            default:
                return SlashCommandResult.Handled(["Usage: /trust [always|never]"]);
        }
    }

    private static SlashCommandResult ExecuteUsage(WinHarnessOptions options, ChatSession session)
    {
        if (session.IsEphemeral)
        {
            return SlashCommandResult.Handled(["/usage requires a persisted session (usage is read from session entries)."]);
        }

        return SlashCommandResult.Handled(
            [UsageFooter.Format(session, options, UsageFooter.FindLastTurnUsage(session))]);
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

    private static async ValueTask<SlashCommandResult> ExecuteCloneAsync(ChatSession session, SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/clone");
        }

        return await SlashCommandAdvanced.CloneAsync(session, context).ConfigureAwait(false);
    }

    private static async ValueTask<SlashCommandResult> ExecuteImportAsync(
        ChatSession session,
        string argument,
        SlashCommandContext? context)
    {
        if (context is null)
        {
            return MissingContext("/import");
        }

        return await SlashCommandAdvanced.ImportAsync(session, argument, context).ConfigureAwait(false);
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
            "/models [provider]    List all models across providers; use /models picker to switch provider+model at once",
            "/provider <id>        Switch active provider",
            "/model <id>           Switch model within current provider; no arg = picker (current provider only)",
            "/skills               List discovered skills",
            "/skill <name|off>     Select a skill for the session (or clear it)",
            "/markdown             Toggle markdown rendering",
            "/tree                 Navigate session branch",
            "/fork                 Copy active branch to a new session file",
            "/effort [level]        Show or set reasoning effort (none/low/medium/high/extra-high)",
            "/compact [text]       Summarize older context and keep recent messages",
            "/usage                Show model, context %, and token usage totals",
            "/trust [always|never]  Save a project trust decision for this folder",
            "/templates            List prompt templates",
            "/t <name> [args]      Expand a prompt template and run it",
            "/clone                Copy the active branch into a new session file",
            "/export [file]        Export the active branch to HTML or JSONL",
            "/import <file.jsonl>  Import a JSONL session file and switch to it",
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
        // Bare "/models" lists every provider's models in one grouped picker;
        // "/models <providerId>" filters to a single provider (backward compat).
        string? filterProviderId = argument.Length > 0 ? argument : null;
        if (filterProviderId is not null && FindProvider(options, filterProviderId) is null)
        {
            return SlashCommandResult.Handled([$"Provider '{filterProviderId}' is not configured."]);
        }

        if (options.Providers.Count == 0)
        {
            return SlashCommandResult.Handled(["No providers configured. Run 'winharness config wizard'."]);
        }

        List<ModelChoice> choices = BuildAllModelChoices(options, filterProviderId, session.ProviderId, session.ModelId);
        if (choices.Count == 0)
        {
            string scope = filterProviderId is null ? "any provider" : $"'{filterProviderId}'";
            return SlashCommandResult.Handled([$"No models configured for {scope}."]);
        }

        if (!IsInteractive)
        {
            return SlashCommandResult.Handled(CreateModelLines(options, filterProviderId, session.ProviderId, session.ModelId));
        }

        // Group choices by provider so the picker shows one scrollable list with
        // provider headers; only model leaves are selectable (SelectionMode.Leaf).
        SelectionPrompt<ModelChoice> prompt = new()
        {
            Title = "Select a model (Esc to cancel)"
        };
        prompt.Mode(SelectionMode.Leaf)
              .PageSize(10)
              .MoreChoicesText("[grey](move up and down to reveal more models)[/]")
              .UseConverter(static choice => choice.Display);

        ModelChoice? active = choices.FirstOrDefault(c =>
            string.Equals(c.ProviderId, session.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.ModelId, session.ModelId, StringComparison.OrdinalIgnoreCase));
        if (active is not null)
        {
            prompt.DefaultValue(active);
        }

        foreach (IGrouping<string, ModelChoice> group in choices.GroupBy(c => c.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            ProviderOptions provider = options.Providers.First(p =>
                string.Equals(p.Id, group.Key, StringComparison.OrdinalIgnoreCase));
            string header = provider.BaseUrl is null
                ? Markup.Escape(provider.Id)
                : $"{Markup.Escape(provider.Id)} [dim]{Markup.Escape(provider.BaseUrl)}[/]";
            prompt.AddChoiceGroup(new ModelChoice(provider.Id, string.Empty, header), group);
        }

        ModelChoice selected = AnsiConsole.Prompt(prompt);

        // If the user re-selected the active provider+model, no-op.
        if (string.Equals(selected.ProviderId, session.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(selected.ModelId, session.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            return SlashCommandResult.Handled([$"Already on model '{selected.ModelId}'."]);
        }

        // Switch provider (when crossing providers) then model in one step.
        if (!string.Equals(selected.ProviderId, session.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            session.ProviderId = selected.ProviderId;
        }

        session.ModelId = selected.ModelId;
        await AppendModelChangeIfPersistedAsync(session, context).ConfigureAwait(false);
        return SlashCommandResult.Handled([$"Provider {session.ProviderId}, model {session.ModelId}."]);
    }

    /// <summary>
    /// Builds the flat, ordered list of selectable model choices across all
    /// configured providers (or a single filtered provider). Each choice carries
    /// its provider id and model alias so selection is unambiguous even when two
    /// providers share an alias; the display string is tagged with the provider
    /// id only on actual collisions to keep the common case clean.
    /// </summary>
    private static List<ModelChoice> BuildAllModelChoices(
        WinHarnessOptions options,
        string? filterProviderId,
        string currentProviderId,
        string currentModelId)
    {
        List<ModelChoice> choices = new();
        HashSet<string> seenDisplays = new(StringComparer.Ordinal);

        foreach (ProviderOptions provider in options.Providers)
        {
            if (filterProviderId is not null &&
                !string.Equals(provider.Id, filterProviderId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (provider.Models.Count == 0)
            {
                continue;
            }

            foreach (ModelOptions model in provider.Models)
            {
                bool isActive = string.Equals(provider.Id, currentProviderId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase);
                string marker = isActive ? "*" : " ";
                string baseDisplay = $"{marker} {model.Id} {model.ProviderModelId}";

                // Disambiguate alias collisions across providers by tagging the
                // later occurrence with its provider id (both stay visible/selectable).
                string display = seenDisplays.Add(baseDisplay)
                    ? baseDisplay
                    : $"{baseDisplay} ({provider.Id})";

                choices.Add(new ModelChoice(provider.Id, model.Id, display));
            }
        }

        return choices;
    }

    /// <summary>
    /// A selectable model entry: the provider id and model alias identify the
    /// target unambiguously; <see cref="Display"/> is what the picker renders.
    /// </summary>
    private sealed record ModelChoice(string ProviderId, string ModelId, string Display);

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

    private static IReadOnlyList<string> CreateModelLines(
        WinHarnessOptions options,
        string? filterProviderId,
        string currentProviderId,
        string currentModelId)
    {
        if (options.Providers.Count == 0)
        {
            return ["No providers configured. Run 'winharness config wizard'."];
        }

        List<string> lines = new();
        bool anyProviderEmitted = false;

        foreach (ProviderOptions provider in options.Providers)
        {
            if (filterProviderId is not null &&
                !string.Equals(provider.Id, filterProviderId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            anyProviderEmitted = true;
            lines.Add($"{provider.Id}  {provider.BaseUrl}");

            if (provider.Models.Count == 0)
            {
                lines.Add($"  No models configured for '{provider.Id}'.");
                continue;
            }

            foreach (ModelOptions model in provider.Models)
            {
                bool isActive = string.Equals(provider.Id, currentProviderId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase);
                string marker = isActive ? "*" : " ";
                lines.Add($"  {marker} {model.Id} {model.ProviderModelId}");
            }
        }

        if (!anyProviderEmitted)
        {
            return [$"Provider '{filterProviderId}' is not configured."];
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

        // "model:effort" shorthand, e.g. "/model gpt-primary:high".
        string? shorthandEffort = null;
        int separator = modelId.LastIndexOf(':');
        if (separator > 0 && separator < modelId.Length - 1)
        {
            string suffix = modelId[(separator + 1)..].ToLowerInvariant();
            if (suffix is "off" or "minimal" or "low" or "medium" or "high")
            {
                shorthandEffort = suffix;
                modelId = modelId[..separator];
            }
        }

        ModelOptions? model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return SlashCommandResult.Handled([$"Model '{modelId}' is not configured for '{session.ProviderId}'."]);
        }

        session.ModelId = model.Id;
        if (shorthandEffort is not null)
        {
            session.ReasoningEffort = shorthandEffort == "off" ? null : shorthandEffort;
        }

        await AppendModelChangeIfPersistedAsync(session, context).ConfigureAwait(false);
        string effortNote = shorthandEffort is null ? "" : $" (effort {shorthandEffort})";
        return SlashCommandResult.Handled([$"Model {session.ModelId}{effortNote}."]);
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

    private static SlashCommandResult SetEffort(ChatSession session, string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            string current = session.ReasoningEffort ?? "default (provider default or model default)";
            return SlashCommandResult.Handled([$"Current reasoning effort: {current}"]);
        }

        string normalized = argument.Trim().ToLowerInvariant();
        if (normalized is "none" or "low" or "medium" or "high" or "extra-high" or "extrahigh" or "default")
        {
            session.ReasoningEffort = normalized == "default" ? null : normalized;
            string setMsg = normalized == "default" ? "Reset reasoning effort to default." : $"Reasoning effort set to '{normalized}'.";
            return SlashCommandResult.Handled([setMsg]);
        }

        return SlashCommandResult.Handled(["Unknown effort level. Valid values: none, low, medium, high, extra-high, default"]);
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

internal sealed record SlashCommandResult(bool ShouldExit, IReadOnlyList<string> Messages, string? ExpandedPrompt = null)
{
    public static SlashCommandResult Handled(IReadOnlyList<string> messages) => new(false, messages);

    public static SlashCommandResult Exit() => new(true, []);

    public static SlashCommandResult Expanded(string prompt) => new(false, [], prompt);
}