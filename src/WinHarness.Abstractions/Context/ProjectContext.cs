namespace WinHarness.Context;

/// <summary>
/// Project context loaded from AGENTS.md, CLAUDE.md, and .winharness system files.
/// </summary>
public sealed record ProjectContext(
    string? SystemPromptReplacement,
    string? SystemPromptAppend,
    string AgentsInstructions);