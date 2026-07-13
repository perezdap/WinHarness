namespace WinHarness.Cli.Chat;

/// <summary>
/// A single slash command entry: its canonical name, an optional argument hint
/// used in help text, and a short description. The catalog is the single source
/// of truth shared by <c>/help</c> and the interactive command palette so the
/// two never drift apart.
/// </summary>
internal sealed record SlashCommandInfo(string Name, string ArgsHint, string Description);

/// <summary>
/// The canonical list of slash commands surfaced to users. Ordering here drives
/// both the <c>/help</c> listing and the interactive palette (bare <c>/</c>).
/// </summary>
internal static class SlashCommandCatalog
{
    public static IReadOnlyList<SlashCommandInfo> Commands { get; } =
    [
        new("/help", "", "Show the command list"),
        new("/session", "", "Show session file, id, name, and status"),
        new("/name", "<name>", "Set session display name (persisted sessions)"),
        new("/new", "", "Start a new persisted session file"),
        new("/resume", "", "Pick a saved session to open"),
        new("/delete", "[id-or-path]", "Delete a session file (trashed by default)"),
        new("/providers", "", "List/select a configured provider"),
        new("/models", "[provider]", "Pick a model across providers (switches provider+model)"),
        new("/provider", "<id>", "Switch active provider"),
        new("/model", "<id>", "Switch model within current provider; no arg = picker"),
        new("/skills", "", "List/select a discovered skill"),
        new("/skill", "<name|off>", "Select a skill for the session (or clear it)"),
        new("/markdown", "", "Toggle markdown rendering"),
        new("/tree", "", "Navigate session branch"),
        new("/fork", "", "Copy active branch to a new session file"),
        new("/clone", "", "Copy the active branch into a new session file"),
        new("/effort", "[level]", "Show or set reasoning effort (none/low/medium/high/extra-high)"),
        new("/compact", "[text]", "Summarize older context and keep recent messages"),
        new("/usage", "", "Show model, context %, and token usage totals"),
        new("/trust", "[always|never]", "Save a project trust decision for this folder"),
        new("/templates", "", "List prompt templates"),
        new("/t", "<name> [args]", "Expand a prompt template and run it"),
        new("/export", "[file]", "Export the active branch to HTML or JSONL"),
        new("/import", "<file.jsonl>", "Import a JSONL session file and switch to it"),
        new("/clear", "", "Clear the in-memory conversation view"),
        new("/exit", "", "Leave the session"),
        new("/quit", "", "Leave the session"),
    ];

    /// <summary>
    /// Formats the catalog as aligned help lines for <c>/help</c>.
    /// </summary>
    public static IReadOnlyList<string> ToHelpLines()
    {
        int width = 0;
        foreach (SlashCommandInfo command in Commands)
        {
            int length = command.Name.Length + (command.ArgsHint.Length == 0 ? 0 : command.ArgsHint.Length + 1);
            if (length > width)
            {
                width = length;
            }
        }

        List<string> lines = new(Commands.Count + 1);
        foreach (SlashCommandInfo command in Commands)
        {
            string invocation = command.ArgsHint.Length == 0
                ? command.Name
                : $"{command.Name} {command.ArgsHint}";
            lines.Add($"{invocation.PadRight(width)}  {command.Description}");
        }

        lines.Add(
            "Esc or Ctrl+C aborts a running turn (Ctrl+C on an empty prompt exits). Type '/' alone to open the command picker.");
        return lines;
    }
}
