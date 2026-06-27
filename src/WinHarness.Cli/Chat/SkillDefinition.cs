namespace WinHarness.Cli.Chat;

internal sealed record SkillDefinition(
    string Name,
    string Description,
    string FilePath,
    string Content)
{
    public string SystemPrompt => $"""
Skill selected: {Name}

Description: {Description}

Use the following skill instructions for this turn when relevant. They supplement, but do not override, higher-priority system/developer/tool instructions.

{Content}
""";
}
