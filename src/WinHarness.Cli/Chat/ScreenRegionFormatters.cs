using WinHarness.Configuration;
using WinHarness.Tools;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Builds the plain-text header line for the fixed top row of
/// <see cref="Rendering.ScreenRegionController"/>: a condensed banner
/// (<c>WinHarness chat · provider · model · effort</c>). Read fresh on each
/// idle prompt so <c>/model</c> and <c>/effort</c> changes are reflected.
/// Plain text only; the fixed row is written via raw <c>Console.Write</c>, so
/// Spectre markup would render literally. Bar colors are applied by the
/// controller, not here.
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
/// Builds the plain-text footer status line for the fixed status row:
/// <c>~/project · md on · context: AGENTS.md · tools allow:read_file</c>.
/// Leads with the workspace path (home shortened to <c>~</c>) so the cwd stays
/// visible when the row is truncated. Plain text only; bar colors are applied
/// by the controller.
/// </summary>
internal static class ScreenFooterFormatter
{
    /// <summary>
    /// Formats the footer status. Always includes the shortened workspace path
    /// and markdown toggle; context and tool-filter segments are omitted when
    /// absent.
    /// </summary>
    public static string Format(ChatSession session)
    {
        List<string> parts = [ShortenPath(session.WorkspaceRoot)];

        parts.Add(session.RenderMarkdown ? "md on" : "md off");

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

    /// <summary>
    /// Replaces a leading user-profile prefix with <c>~</c> so the cwd fits the
    /// status bar. Leaves the path unchanged when it is not under the profile.
    /// </summary>
    internal static string ShortenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ".";
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)
            && full.StartsWith(home, StringComparison.OrdinalIgnoreCase))
        {
            string tail = full[home.Length..];
            if (tail.Length == 0)
            {
                return "~";
            }

            // Keep the directory separator that follows the home prefix.
            return "~" + tail;
        }

        return full;
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
