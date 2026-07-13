using WinHarness.Configuration;
using WinHarness.Tools;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Builds the plain-text header line for the fixed top row of
/// <see cref="Rendering.ScreenRegionController"/>: a condensed banner
/// (<c>WinHarness chat · provider · model · effort</c>). Read fresh on each
/// idle prompt so <c>/model</c> and <c>/effort</c> changes are reflected.
/// Plain text only; the fixed row is written via raw <c>Console.Write</c>, so
/// Spectre markup would render literally.
/// </summary>
internal static class ScreenHeaderFormatter
{
    /// <summary>
    /// Formats the header, e.g.
    /// <c>WinHarness chat · openai · gpt-4o · effort medium</c>.
    /// </summary>
    public static string Format(ChatSession session, WinHarnessOptions options)
    {
        string effort = StatusLineFormatter.ResolveEffort(session, options);
        return $"WinHarness chat · {session.ProviderId} · {session.ModelId} · effort {effort}";
    }
}

/// <summary>
/// Builds the plain-text footer status line for the fixed bottom row:
/// <c>md on · context: AGENTS.md · SYSTEM.md · tools allow:read_file</c>.
/// Holds only the status bits that are not already in the header (markdown,
/// context files, tool filter). Plain text only.
/// </summary>
internal static class ScreenFooterFormatter
{
    /// <summary>
    /// Formats the footer status. Always includes the markdown toggle; context
    /// and tool-filter segments are omitted when absent.
    /// </summary>
    public static string Format(ChatSession session)
    {
        List<string> parts = [session.RenderMarkdown ? "md on" : "md off"];

        string? context = ContextBannerFormatter.Format(session.ProjectContext);
        if (!string.IsNullOrWhiteSpace(context))
        {
            parts.Add(context);
        }

        string? tools = FormatToolFilter(session.ToolFilter);
        if (!string.IsNullOrWhiteSpace(tools))
        {
            parts.Add(tools);
        }

        return string.Join(" · ", parts);
    }

    private static string? FormatToolFilter(ToolFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        if (filter.DisableAll)
        {
            return "tools: all disabled";
        }

        List<string> segments = [];
        if (filter.Allow is { Count: > 0 } allow)
        {
            segments.Add($"allow:{string.Join(",", allow)}");
        }

        if (filter.Exclude is { Count: > 0 } exclude)
        {
            segments.Add($"exclude:{string.Join(",", exclude)}");
        }

        return segments.Count == 0 ? null : "tools " + string.Join(" ", segments);
    }
}
