using WinHarness.Configuration;

namespace WinHarness.Cli.Chat;

internal static class SlashCommandProcessor
{
    public static SlashCommandResult Execute(WinHarnessOptions options, ChatSession session, string input)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        string argument = parts.Length > 1 ? parts[1] : string.Empty;

        return command switch
        {
            "/exit" or "/quit" => SlashCommandResult.Exit(),
            "/help" => SlashCommandResult.Handled(CreateHelpLines()),
            "/providers" => SlashCommandResult.Handled(CreateProviderLines(options, session.ProviderId)),
            "/models" => SlashCommandResult.Handled(CreateModelLines(options, argument.Length > 0 ? argument : session.ProviderId, session.ModelId)),
            "/provider" => SwitchProvider(options, session, argument),
            "/model" => SwitchModel(options, session, argument),
            "/skills" => SlashCommandResult.Handled(CreateSkillLines(session)),
            "/skill" => SelectSkill(session, argument),
            "/markdown" => ToggleMarkdown(session),
            "/new" or "/clear" => Clear(session),
            _ => SlashCommandResult.Handled([$"Unknown command '{command}'. Try /help."])
        };
    }

    private static IReadOnlyList<string> CreateHelpLines()
    {
        return
        [
            "/help                 Show this help",
            "/providers            List configured providers",
            "/models [provider]    List models for a provider",
            "/provider <id>        Switch active provider",
            "/model <id>           Switch active model",
            "/skills               List discovered skills",
            "/skill <name|off>     Select a skill for the session (or clear it)",
            "/markdown             Toggle markdown rendering",
            "/new, /clear          Reset the conversation",
            "/exit, /quit          Leave the session"
        ];
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

    private static SlashCommandResult SelectSkill(ChatSession session, string argument)
    {
        if (argument.Length == 0)
        {
            return SlashCommandResult.Handled(CreateSkillLines(session));
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

    private static SlashCommandResult SwitchProvider(WinHarnessOptions options, ChatSession session, string providerId)
    {
        if (providerId.Length == 0)
        {
            return SlashCommandResult.Handled(CreateProviderLines(options, session.ProviderId));
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

        return SlashCommandResult.Handled([$"Provider {session.ProviderId}, model {session.ModelId}."]);
    }

    private static SlashCommandResult SwitchModel(WinHarnessOptions options, ChatSession session, string modelId)
    {
        ProviderOptions? provider = FindProvider(options, session.ProviderId);
        if (provider is null)
        {
            return SlashCommandResult.Handled([$"Provider '{session.ProviderId}' is not configured."]);
        }

        if (modelId.Length == 0)
        {
            return SlashCommandResult.Handled(CreateModelLines(options, session.ProviderId, session.ModelId));
        }

        ModelOptions? model = provider.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return SlashCommandResult.Handled([$"Model '{modelId}' is not configured for '{session.ProviderId}'."]);
        }

        session.ModelId = model.Id;
        return SlashCommandResult.Handled([$"Model {session.ModelId}."]);
    }

    private static SlashCommandResult ToggleMarkdown(ChatSession session)
    {
        session.RenderMarkdown = !session.RenderMarkdown;
        return SlashCommandResult.Handled([$"Markdown rendering {(session.RenderMarkdown ? "on" : "off")}."]);
    }

    private static SlashCommandResult Clear(ChatSession session)
    {
        session.Conversation.Clear();
        return SlashCommandResult.Handled(["Conversation cleared."]);
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
