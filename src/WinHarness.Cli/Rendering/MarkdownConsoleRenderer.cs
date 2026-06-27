using System.Text;
using Spectre.Console;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Lightweight markdown-to-terminal renderer with inline styling, headings,
/// lists, blockquotes, rules, and syntax-aware code fences.
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
            string trimmedStart = line.TrimStart();

            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
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
                    fenceLanguage = trimmedStart[3..].Trim();
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
        string trimmed = line.TrimEnd();

        if (trimmed.Length == 0)
        {
            AnsiConsole.WriteLine();
            return;
        }

        // Horizontal rule: ---, ***, ___
        if (IsHorizontalRule(trimmed))
        {
            AnsiConsole.Write(new Rule().RuleStyle("grey"));
            return;
        }

        // Headings (#, ##, ###, ####).
        if (trimmed.StartsWith("#### ", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[bold]" + RenderInline(trimmed[5..]) + "[/]");
            return;
        }

        if (trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[bold deepskyblue1]" + RenderInline(trimmed[4..]) + "[/]");
            return;
        }

        if (trimmed.StartsWith("## ", StringComparison.Ordinal))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold underline deepskyblue1]" + RenderInline(trimmed[3..]) + "[/]");
            return;
        }

        if (trimmed.StartsWith("# ", StringComparison.Ordinal))
        {
            AnsiConsole.Write(new Rule("[bold deepskyblue1]" + RenderInline(trimmed[2..]) + "[/]").LeftJustified());
            return;
        }

        // Blockquote.
        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine("[grey]\u2502[/] [italic grey]" + RenderInline(trimmed[2..]) + "[/]");
            return;
        }

        // Indentation-aware list handling.
        int indent = line.Length - line.TrimStart().Length;
        string content = line.TrimStart();
        string pad = new(' ', indent);

        // Ordered list: "1. ", "23) ".
        int orderedMarker = OrderedListMarkerLength(content);
        if (orderedMarker > 0)
        {
            string number = content[..(orderedMarker - 1)].Trim();
            string rest = content[orderedMarker..];
            AnsiConsole.MarkupLine($"{pad}  [bold yellow]{Markup.Escape(number)}.[/] " + RenderInline(rest));
            return;
        }

        // Unordered list: "- ", "* ", "+ ".
        if (content.StartsWith("- ", StringComparison.Ordinal) ||
            content.StartsWith("* ", StringComparison.Ordinal) ||
            content.StartsWith("+ ", StringComparison.Ordinal))
        {
            string bullet = indent >= 2 ? "[grey]\u25e6[/]" : "[green]\u2022[/]";
            AnsiConsole.MarkupLine($"{pad}  {bullet} " + RenderInline(content[2..]));
            return;
        }

        AnsiConsole.MarkupLine(RenderInline(line));
    }

    /// <summary>
    /// Converts inline markdown (bold, italic, inline code, links, strikethrough)
    /// to Spectre.Console markup. All literal text is escaped.
    /// </summary>
    private static string RenderInline(string text)
    {
        StringBuilder result = new(text.Length + 16);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // Inline code: `code`
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    string code = text[(i + 1)..end];
                    result.Append("[black on grey85] ").Append(Markup.Escape(code)).Append(" [/]");
                    i = end + 1;
                    continue;
                }
            }

            // Bold: **text** or __text__
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                string delim = new(c, 2);
                int end = text.IndexOf(delim, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    string inner = text[(i + 2)..end];
                    result.Append("[bold]").Append(RenderInline(inner)).Append("[/]");
                    i = end + 2;
                    continue;
                }
            }

            // Italic: *text* or _text_
            if (c == '*' || c == '_')
            {
                int end = text.IndexOf(c, i + 1);
                if (end > i && end != i + 1)
                {
                    string inner = text[(i + 1)..end];
                    result.Append("[italic]").Append(RenderInline(inner)).Append("[/]");
                    i = end + 1;
                    continue;
                }
            }

            // Strikethrough: ~~text~~
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    string inner = text[(i + 2)..end];
                    result.Append("[strikethrough]").Append(RenderInline(inner)).Append("[/]");
                    i = end + 2;
                    continue;
                }
            }

            // Link: [text](url)
            if (c == '[')
            {
                int closeText = text.IndexOf(']', i + 1);
                if (closeText > i && closeText + 1 < text.Length && text[closeText + 1] == '(')
                {
                    int closeUrl = text.IndexOf(')', closeText + 2);
                    if (closeUrl > closeText)
                    {
                        string label = text[(i + 1)..closeText];
                        string url = text[(closeText + 2)..closeUrl];
                        result.Append("[link=").Append(Markup.Escape(url)).Append("][underline blue]")
                            .Append(RenderInline(label)).Append("[/][/]");
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }

            result.Append(Markup.Escape(c.ToString()));
            i++;
        }

        return result.ToString();
    }

    private static bool IsHorizontalRule(string trimmed)
    {
        if (trimmed.Length < 3)
        {
            return false;
        }

        char first = trimmed[0];
        if (first != '-' && first != '*' && first != '_')
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c != first && c != ' ')
            {
                return false;
            }
        }

        return trimmed.Count(c => c == first) >= 3;
    }

    private static int OrderedListMarkerLength(string content)
    {
        int digits = 0;
        while (digits < content.Length && char.IsAsciiDigit(content[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits + 1 >= content.Length)
        {
            return 0;
        }

        char delimiter = content[digits];
        if ((delimiter == '.' || delimiter == ')') && content[digits + 1] == ' ')
        {
            return digits + 2;
        }

        return 0;
    }

    private static void WriteCodeFence(string language, IReadOnlyList<string> lines)
    {
        string code = string.Join(Environment.NewLine, lines.Select(line => HighlightCode(line, language)));
        Panel panel = new Panel(code)
            .Header(string.IsNullOrWhiteSpace(language) ? " code " : $" {language} ")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey)
            .Padding(1, 0);
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
