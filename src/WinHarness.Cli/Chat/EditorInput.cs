using System.Text;
using System.Text.RegularExpressions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Line-based editor input helpers: triple-quote multi-line continuation,
/// <c>!</c>/<c>!!</c> command escapes, and <c>@file</c> expansion.
/// </summary>
internal static partial class EditorInput
{
    /// <summary>
    /// Marker that opens (and closes) a multi-line block.
    /// </summary>
    public const string MultiLineMarker = "\"\"\"";

    /// <summary>
    /// Classifies a submitted line.
    /// </summary>
    public static EditorInputKind Classify(string line)
    {
        if (line.StartsWith(MultiLineMarker, StringComparison.Ordinal))
        {
            return EditorInputKind.MultiLineStart;
        }

        if (line.StartsWith("!!", StringComparison.Ordinal))
        {
            return EditorInputKind.CommandLocal;
        }

        if (line.StartsWith('!'))
        {
            return EditorInputKind.CommandToModel;
        }

        return EditorInputKind.Prompt;
    }

    /// <summary>
    /// Strips the command-escape prefix (<c>!</c> or <c>!!</c>).
    /// </summary>
    public static string StripCommandPrefix(string line)
    {
        return line.StartsWith("!!", StringComparison.Ordinal)
            ? line[2..].Trim()
            : line[1..].Trim();
    }

    /// <summary>
    /// Formats captured command output as a user message for the model.
    /// </summary>
    public static string FormatCommandOutputForModel(string command, string stdout, string stderr, int exitCode)
    {
        StringBuilder builder = new();
        builder.Append("I ran `").Append(command).Append("` (exit code ").Append(exitCode).AppendLine("):");
        builder.AppendLine("```");
        if (stdout.Length > 0)
        {
            builder.AppendLine(stdout.TrimEnd());
        }

        if (stderr.Length > 0)
        {
            builder.AppendLine("--- stderr ---");
            builder.AppendLine(stderr.TrimEnd());
        }

        builder.Append("```");
        return builder.ToString();
    }

    /// <summary>
    /// Expands <c>@path</c> tokens in a prompt to fenced file contents appended
    /// after the prompt text. Tokens whose path does not exist are left in
    /// place. Returns the expanded prompt plus the list of attached paths.
    /// </summary>
    public static (string Prompt, IReadOnlyList<string> Attached) ExpandFileReferences(
        string prompt,
        string workspaceRoot,
        long maxFileBytes = 256 * 1024)
    {
        List<string> attached = [];
        StringBuilder attachments = new();

        string replaced = FileReferencePattern().Replace(prompt, match =>
        {
            string token = match.Groups["path"].Value;
            string fullPath = Path.IsPathRooted(token) ? token : Path.GetFullPath(Path.Combine(workspaceRoot, token));
            FileInfo file = new(fullPath);
            if (!file.Exists)
            {
                return match.Value;
            }

            if (file.Length > maxFileBytes)
            {
                attachments.AppendLine()
                    .Append("[file ").Append(token).Append(" skipped: ")
                    .Append(file.Length).Append(" bytes exceeds the ").Append(maxFileBytes).AppendLine(" byte limit]");
                return token;
            }

            attached.Add(token);
            attachments.AppendLine()
                .Append("```").AppendLine(token)
                .AppendLine(File.ReadAllText(fullPath).TrimEnd())
                .AppendLine("```");
            return token;
        });

        return attachments.Length == 0
            ? (prompt, attached)
            : (replaced + Environment.NewLine + attachments, attached);
    }

    // "@" followed by a path: letters, digits, and common path punctuation.
    // Requires start-of-string or whitespace before "@" so emails and
    // decorators ("user@host", "code @attribute") are not treated as files.
    [GeneratedRegex(@"(?<=^|\s)@(?<path>[\w./\\:~-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FileReferencePattern();
}

/// <summary>
/// Kind of editor input submitted.
/// </summary>
internal enum EditorInputKind
{
    /// <summary>Regular prompt text.</summary>
    Prompt,

    /// <summary>Opens a multi-line block terminated by another marker.</summary>
    MultiLineStart,

    /// <summary>Runs a command and sends its output to the model.</summary>
    CommandToModel,

    /// <summary>Runs a command locally without involving the model.</summary>
    CommandLocal
}
