using Spectre.Console;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Minimal markdown renderer for terminal output.
/// </summary>
internal static class MarkdownConsoleRenderer
{
    public static void Write(string markdown)
    {
        bool inFence = false;
        string fenceLanguage = string.Empty;
        List<string> fenceLines = [];

        foreach (string line in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    WriteCodeFence(fenceLanguage, fenceLines);
                    fenceLines.Clear();
                    fenceLanguage = string.Empty;
                    inFence = false;
                }
                else
                {
                    fenceLanguage = line[3..].Trim();
                    inFence = true;
                }

                continue;
            }

            if (inFence)
            {
                fenceLines.Add(line);
                continue;
            }

            WriteMarkdownLine(line);
        }

        if (inFence)
        {
            WriteCodeFence(fenceLanguage, fenceLines);
        }
    }

    private static void WriteMarkdownLine(string line)
    {
        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            AnsiConsole.Write(new Rule("[bold]" + Markup.Escape(line[2..]) + "[/]"));
            return;
        }

        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[bold underline]" + Markup.Escape(line[3..]) + "[/]");
            return;
        }

        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("  [green]•[/] " + Markup.Escape(line[2..]));
            return;
        }

        AnsiConsole.MarkupLine(Markup.Escape(line));
    }

    private static void WriteCodeFence(string language, IReadOnlyList<string> lines)
    {
        string code = string.Join(Environment.NewLine, lines.Select(line => HighlightCode(line, language)));
        Panel panel = new(code)
            .Header(string.IsNullOrWhiteSpace(language) ? "code" : language)
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
    }

    private static string HighlightCode(string line, string language)
    {
        string escaped = Markup.Escape(line);
        if (!IsCSharpLike(language))
        {
            return escaped;
        }

        string[] keywords = ["using", "namespace", "public", "private", "internal", "sealed", "class", "record", "interface", "return", "new", "async", "await"];
        foreach (string keyword in keywords)
        {
            escaped = escaped.Replace(keyword, "[blue]" + keyword + "[/]", StringComparison.Ordinal);
        }

        return escaped;
    }

    private static bool IsCSharpLike(string language)
    {
        return string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(language, "cs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(language, "c#", StringComparison.OrdinalIgnoreCase);
    }
}
