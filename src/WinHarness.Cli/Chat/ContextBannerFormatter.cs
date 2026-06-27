using WinHarness.Context;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Formats loaded project context for the chat startup banner.
/// </summary>
internal static class ContextBannerFormatter
{
    /// <summary>
    /// Builds a banner line such as <c>context: AGENTS.md · SYSTEM.md (project)</c>.
    /// Returns <see langword="null"/> when no context files were loaded.
    /// </summary>
    public static string? Format(ProjectContext context)
    {
        List<string> parts = [];

        if (!string.IsNullOrWhiteSpace(context.AgentsInstructions))
        {
            parts.Add("AGENTS.md");
        }

        if (!string.IsNullOrWhiteSpace(context.SystemPromptReplacement))
        {
            parts.Add("SYSTEM.md");
        }

        if (!string.IsNullOrWhiteSpace(context.SystemPromptAppend))
        {
            parts.Add("APPEND_SYSTEM.md");
        }

        return parts.Count == 0 ? null : "context: " + string.Join(" · ", parts);
    }
}