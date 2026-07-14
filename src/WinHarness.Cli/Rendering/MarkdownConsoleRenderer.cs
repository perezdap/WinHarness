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
    /// <summary>
    /// HTML block tags whose opening/closing lines are dropped entirely (the
    /// tag and any attributes are not rendered). Nested inline content on
    /// subsequent lines is left intact so a <c>&lt;details&gt;&lt;summary&gt;…
    /// &lt;/summary&gt;…&lt;/details&gt;</c> block still shows its inner
    /// markdown instead of raw HTML.
    /// </summary>
    private static readonly HashSet<string> HtmlBlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "details", "summary", "section", "article", "aside", "div", "figure",
        "figcaption", "blockquote", "table", "thead", "tbody", "tfoot", "tr",
        "ul", "ol", "li", "p", "br", "hr", "span",
    };

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

            // HTML block tags (e.g. <details>, <summary>, </details>) render as
            // nothing — models often wrap collapsible/markup in raw HTML that a
            // terminal cannot display. Skip the whole line so it does not leak
            // as literal text. Inline content on other lines is handled by
            // RenderInline's tag stripping.
            if (IsHtmlBlockTagLine(trimmedStart))
            {
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

    /// <summary>
    /// Returns <c>true</c> when <paramref name="trimmedLine"/> consists solely
    /// of a single HTML tag from <see cref="HtmlBlockTags"/> (optionally
    /// self-closing and/or with attributes), possibly surrounded by whitespace.
    /// Matches <c>&lt;details&gt;</c>, <c>&lt;/details&gt;</c>,
    /// <c>&lt;summary&gt;…&lt;/summary&gt;</c> on one line, and
    /// <c>&lt;br /&gt;</c>, but not prose containing an inline tag.
    /// </summary>
    private static bool IsHtmlBlockTagLine(string trimmedLine)
    {
        if (trimmedLine.Length < 3 || trimmedLine[0] != '<')
        {
            return false;
        }

        // Reject lines with content after the closing '>' (e.g. "<li>item") so
        // inline-wrapped content is not dropped — RenderInline strips the tag.
        int close = trimmedLine.IndexOf('>');
        if (close < 0 || close != trimmedLine.Length - 1)
        {
            return false;
        }

        // Inner text between < and > (no angle brackets), after an optional '/'.
        ReadOnlySpan<char> inner = trimmedLine.AsSpan(1, close - 1);
        inner = inner.Trim();
        if (inner.Length == 0)
        {
            return false;
        }

        bool isClosing = inner[0] == '/';
        if (isClosing)
        {
            inner = inner[1..];
        }

        // Tag name ends at the first whitespace or '/'. Attributes follow.
        int nameEnd = 0;
        while (nameEnd < inner.Length && !char.IsWhiteSpace(inner[nameEnd]) && inner[nameEnd] != '/')
        {
            nameEnd++;
        }

        ReadOnlySpan<char> name = inner[..nameEnd];
        if (name.Length == 0)
        {
            return false;
        }

        return HtmlBlockTags.Contains(name.ToString());
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
                        // Markdown links may carry an optional title after the
                        // destination: [text](url "title"). The title (and its
                        // quotes) must not leak into the Spectre [link=...] markup —
                        // Spectre would parse "title" as a style name and throw.
                        // A bare destination cannot contain unencoded whitespace,
                        // so trim from the first space when a titled suffix is
                        // present.
                        url = url.Trim();
                        int titleSpace = url.IndexOf(' ');
                        if (titleSpace >= 0)
                        {
                            url = url[..titleSpace];
                        }

                        result.Append("[link=").Append(Markup.Escape(url)).Append("][underline blue]")
                            .Append(RenderInline(label)).Append("[/][/]");
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }

            // Inline HTML: <strong>, </strong>, <em>, <code>, <br>, etc.
            // Drop the tag entirely so raw HTML never leaks as literal text.
            // Paired tags rely on surrounding markdown (** for bold, ` for
            // code) to convey styling; the tag itself is noise in a terminal.
            if (c == '<')
            {
                int closeTag = text.IndexOf('>', i + 1);
                if (closeTag > i && IsInlineHtmlTag(text.AsSpan(i + 1, closeTag - i - 1)))
                {
                    i = closeTag + 1;
                    continue;
                }
            }

            result.Append(Markup.Escape(c.ToString()));
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="tagContent"/> (the text between
    /// <c>&lt;</c> and <c>&gt;</c>) is a single HTML tag name, optionally closing
    /// (<c>/name</c>), self-closing (<c>name/</code>), or carrying attributes
    /// (<c>name attrs</c>). Tag names must be ASCII letters (rejects <c>&lt;=</c>,
    /// <c>&lt;3</c>, and other less-than prose).
    /// </summary>
    private static bool IsInlineHtmlTag(ReadOnlySpan<char> tagContent)
    {
        ReadOnlySpan<char> inner = tagContent.Trim();
        if (inner.Length == 0)
        {
            return false;
        }

        if (inner[0] == '/')
        {
            inner = inner[1..];
        }

        if (inner.Length == 0 || !char.IsAsciiLetter(inner[0]))
        {
            return false;
        }

        // The tag name is a run of ASCII letters; the rest (whitespace +
        // attributes, or a trailing '/') does not disqualify it.
        int nameEnd = 0;
        while (nameEnd < inner.Length && (char.IsAsciiLetter(inner[nameEnd]) || inner[nameEnd] == '-'))
        {
            nameEnd++;
        }

        return nameEnd > 0;
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
