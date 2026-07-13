using System.Linq;
using WinHarness.Configuration;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Formats the live status line shown above the idle prompt. Reads the session
/// state on every call so provider/model/effort always reflect what is actually
/// set after <c>/model</c> and <c>/effort</c> changes.
/// </summary>
internal static class StatusLineFormatter
{
    /// <summary>
    /// Builds Spectre markup for the compact status line, e.g.
    /// <c>provider · model · effort medium · md on</c>.
    /// </summary>
    public static string FormatMarkup(ChatSession session, WinHarnessOptions options)
    {
        string effort = ResolveEffort(session, options);
        string markdown = session.RenderMarkdown ? "[green]md on[/]" : "[grey]md off[/]";

        return "[dim]"
            + Markup(session.ProviderId)
            + "[/] [dim]·[/] [bold]"
            + Markup(session.ModelId)
            + "[/] [dim]·[/] [dim]effort[/] [bold]"
            + Markup(effort)
            + "[/] [dim]·[/] "
            + markdown;
    }

    /// <summary>
    /// Resolves the reasoning effort actually in use: the session override when
    /// set, otherwise the model's configured default, otherwise "default".
    /// </summary>
    public static string ResolveEffort(ChatSession session, WinHarnessOptions options)
    {
        if (!string.IsNullOrWhiteSpace(session.ReasoningEffort))
        {
            return session.ReasoningEffort!;
        }

        ModelOptions? model = options.Providers
            .FirstOrDefault(p => string.Equals(p.Id, session.ProviderId, StringComparison.OrdinalIgnoreCase))
            ?.Models.FirstOrDefault(m => string.Equals(m.Id, session.ModelId, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(model?.ReasoningEffort) ? "default" : model!.ReasoningEffort!;
    }

    private static string Markup(string value) => Spectre.Console.Markup.Escape(value);
}
