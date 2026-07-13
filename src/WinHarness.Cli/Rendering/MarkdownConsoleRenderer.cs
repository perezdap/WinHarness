using System.Diagnostics.CodeAnalysis;
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

        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
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

            // GFM table: a header row followed by a separator row (| --- | --- |).
            // Detection requires the separator to validate, so prose containing a
            // pipe never accidentally renders as a table.
            if (TryParseTableRow(line, out List<string>? headerCells) &&
                i + 1 < lines.Length &&
                TryParseTableSeparator(lines[i + 1], out List<TableColumnAlignment>? alignments))
            {
                List<List<string>> rows = [];
                int j = i + 2;
                while (j < lines.Length && TryParseTableRow(lines[j], out List<string>? rowCells))
                {
                    rows.Add(rowCells);
                    j++;
                }

                WriteTable(headerCells, alignments, rows);
                i = j - 1;
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

    /// <summary>
    /// Column alignment parsed from a GFM table separator row.
    /// </summary>
    private enum TableColumnAlignment { Default, Left, Center, Right }

    /// <summary>
    /// Splits a line on unescaped <c>|</c> into trimmed cells. Drops the empty
    /// leading/trailing cells produced by leading/trailing pipes so both
    /// <c>| a | b |</c> and <c>a | b</c> parse identically. Returns false when
    /// the line contains no pipe at all.
    /// </summary>
    private static bool TryParseTableRow(string line, [NotNullWhen(true)] out List<string>? cells)
    {
        cells = null;
        string trimmed = line.Trim();
        if (!trimmed.Contains('|'))
        {
            return false;
        }

        List<string> parts = [];
        StringBuilder current = new();
        for (int k = 0; k < trimmed.Length; k++)
        {
            char c = trimmed[k];
            if (c == '\\' && k + 1 < trimmed.Length && trimmed[k + 1] == '|')
            {
                current.Append('|');
                k++;
                continue;
            }

            if (c == '|')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());

        if (parts.Count > 0 && parts[0].Trim().Length == 0)
        {
            parts.RemoveAt(0);
        }

        if (parts.Count > 0 && parts[^1].Trim().Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count == 0)
        {
            return false;
        }

        cells = parts.Select(static p => p.Trim()).ToList();
        return true;
    }

    /// <summary>
    /// Parses a GFM separator row (<c>| :--- | ---: | :---: | --- |</c>) into
    /// per-column alignments. Each cell must be non-empty and contain only
    /// <c>-</c>, <c>:</c>, and spaces, with at least one <c>-</c>.
    /// </summary>
    private static bool TryParseTableSeparator(
        string line,
        [NotNullWhen(true)] out List<TableColumnAlignment>? alignments)
    {
        alignments = null;
        if (!TryParseTableRow(line, out List<string>? cells))
        {
            return false;
        }

        List<TableColumnAlignment> result = [];
        foreach (string raw in cells)
        {
            string cell = raw.Trim();
            if (cell.Length == 0 || !cell.Contains('-'))
            {
                return false;
            }

            foreach (char ch in cell)
            {
                if (ch is not ('-' or ':' or ' '))
                {
                    return false;
                }
            }

            bool leftColon = cell.StartsWith(':');
            bool rightColon = cell.EndsWith(':');
            result.Add((leftColon, rightColon) switch
            {
                (true, true) => TableColumnAlignment.Center,
                (false, true) => TableColumnAlignment.Right,
                (true, false) => TableColumnAlignment.Left,
                _ => TableColumnAlignment.Default
            });
        }

        alignments = result;
        return true;
    }

    /// <summary>
    /// Renders a GFM table as a rounded-border Spectre table. Inline markdown
    /// (bold, code, links) is rendered in each cell. Rows with fewer cells than
    /// the header are padded; extra cells are truncated to the header width.
    /// </summary>
    private static void WriteTable(
        IReadOnlyList<string> header,
        IReadOnlyList<TableColumnAlignment> alignments,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Table table = new Table()
            .Border(TableBorder.Square)
            .BorderColor(Color.Grey);

        int columnCount = header.Count;
        for (int col = 0; col < columnCount; col++)
        {
            TableColumn column = new TableColumn(RenderInline(header[col]));
            TableColumnAlignment align = col < alignments.Count ? alignments[col] : TableColumnAlignment.Default;
            switch (align)
            {
                case TableColumnAlignment.Left:
                    column.LeftAligned();
                    break;
                case TableColumnAlignment.Right:
                    column.RightAligned();
                    break;
                case TableColumnAlignment.Center:
                    column.Centered();
                    break;
            }

            table.AddColumn(column);
        }

        foreach (IReadOnlyList<string> row in rows)
        {
            string[] rendered = new string[columnCount];
            for (int col = 0; col < columnCount; col++)
            {
                rendered[col] = col < row.Count ? RenderInline(row[col]) : string.Empty;
            }

            table.AddRow(rendered);
        }

        AnsiConsole.Write(table);
    }
}
