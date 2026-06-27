using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// REPL interactive picker for <c>/tree</c> branch navigation.
/// </summary>
internal static class SessionTreePicker
{
    /// <summary>
    /// Lists branch entries, prompts for selection, and invokes <paramref name="onBranch"/> on success.
    /// </summary>
    public static IReadOnlyList<string> PickAndBranch(
        ISessionManager sessionManager,
        Action<string> onBranch,
        Func<string?>? readLine = null)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentNullException.ThrowIfNull(onBranch);
        readLine ??= Console.ReadLine;

        IReadOnlyList<SessionTreeChoices.Choice> choices = SessionTreeChoices.BuildChoices(sessionManager);
        if (choices.Count == 0)
        {
            return ["Session tree is empty. Send a message first."];
        }

        List<string> lines =
        [
            "Active branch:",
        ];

        for (int index = 0; index < choices.Count; index++)
        {
            lines.Add(SessionTreeChoices.FormatReplLine(choices[index], index));
        }

        if (sessionManager.LeafEntryId is not null && sessionManager.GetChildren(sessionManager.LeafEntryId).Count > 0)
        {
            lines.Add("(* = active branch, numbered children follow the leaf)");
        }

        lines.Add("branch-to <#> (empty to cancel):");
        Console.WriteLine(string.Join(Environment.NewLine, lines));

        string? input = readLine()?.Trim();
        if (string.IsNullOrEmpty(input))
        {
            return ["Branching cancelled."];
        }

        if (!int.TryParse(input, out int selection) || selection < 1 || selection > choices.Count)
        {
            return [$"Invalid selection '{input}'. Enter a number from 1 to {choices.Count}."];
        }

        return SessionTreeChoices.ApplyBranch(sessionManager, choices[selection - 1].Entry, onBranch);
    }
}