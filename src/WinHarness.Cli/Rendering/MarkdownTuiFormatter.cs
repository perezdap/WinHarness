using System.Text;

namespace WinHarness.Cli.Rendering;

internal enum MarkdownBlockStyle
{
    None,
    Heading1,
    Heading2,
    Heading3,
    Heading4,
    BlockQuote,
    CodeFence,
    HorizontalRule,
}

internal enum MarkdownEmphasis
{
    Bold,
    Italic,
    InlineCode,
    Link,
    Strikethrough,
    ListMarker,
}

internal readonly record struct MarkdownRun(int Start, int Length, MarkdownEmphasis Emphasis);

internal readonly record struct MarkdownDisplayLine(
    string Text,
    MarkdownBlockStyle BlockStyle,
    IReadOnlyList<MarkdownRun> Runs);

/// <summary>
/// Converts markdown into plain transcript lines with emphasis spans suitable for
/// Terminal.Gui rendering. Block structure mirrors <see cref="MarkdownConsoleRenderer"/>.
/// </summary>
internal static class MarkdownTuiFormatter
{
    public static IReadOnlyList<MarkdownDisplayLine> ParseLines(string markdown)
    {
        List<MarkdownDisplayLine> lines = [];
        bool inFence = false;
        string fenceLanguage = string.Empty;

        foreach (string rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmedStart = rawLine.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence)
                {
                    inFence = false;
                    fenceLanguage = string.Empty;
                }
                else
                {
                    fenceLanguage = trimmedStart[3..].Trim();
                    inFence = true;
                    if (fenceLanguage.Length > 0)
                    {
                        lines.Add(new MarkdownDisplayLine(
                            fenceLanguage,
                            MarkdownBlockStyle.CodeFence,
                            []));
                    }
                }

                continue;
            }

            if (inFence)
            {
                lines.Add(new MarkdownDisplayLine(rawLine, MarkdownBlockStyle.CodeFence, []));
                continue;
            }

            lines.Add(ParseMarkdownLine(rawLine));
        }

        return lines;
    }

    public static IEnumerable<(string Text, MarkdownBlockStyle BlockStyle, IReadOnlyList<MarkdownRun> Runs)> WordWrap(
        MarkdownDisplayLine line,
        int width)
    {
        if (line.Text.Length == 0)
        {
            yield return (string.Empty, line.BlockStyle, line.Runs);
            yield break;
        }

        if (line.BlockStyle is MarkdownBlockStyle.CodeFence or MarkdownBlockStyle.HorizontalRule)
        {
            string remaining = line.Text;
            while (remaining.Length > 0)
            {
                int chunkLength = Math.Min(width, remaining.Length);
                yield return (remaining[..chunkLength], line.BlockStyle, []);
                remaining = remaining[chunkLength..];
            }

            yield break;
        }

        foreach ((string chunk, IReadOnlyList<MarkdownRun> runs) in WordWrapRuns(line.Text, line.Runs, width))
        {
            yield return (chunk, line.BlockStyle, runs);
        }
    }

    private static MarkdownDisplayLine ParseMarkdownLine(string line)
    {
        string trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
        {
            return new MarkdownDisplayLine(string.Empty, MarkdownBlockStyle.None, []);
        }

        if (IsHorizontalRule(trimmed))
        {
            return new MarkdownDisplayLine(new string('─', Math.Min(trimmed.Length, 40)), MarkdownBlockStyle.HorizontalRule, []);
        }

        if (trimmed.StartsWith("#### ", StringComparison.Ordinal))
        {
            return ToDisplayLine(trimmed[5..], MarkdownBlockStyle.Heading4);
        }

        if (trimmed.StartsWith("### ", StringComparison.Ordinal))
        {
            return ToDisplayLine(trimmed[4..], MarkdownBlockStyle.Heading3);
        }

        if (trimmed.StartsWith("## ", StringComparison.Ordinal))
        {
            return ToDisplayLine(trimmed[3..], MarkdownBlockStyle.Heading2);
        }

        if (trimmed.StartsWith("# ", StringComparison.Ordinal))
        {
            return ToDisplayLine(trimmed[2..], MarkdownBlockStyle.Heading1);
        }

        if (trimmed.StartsWith("> ", StringComparison.Ordinal))
        {
            (string plain, List<MarkdownRun> runs) = ParseInline(trimmed[2..]);
            return new MarkdownDisplayLine("│ " + plain, MarkdownBlockStyle.BlockQuote, ShiftRuns(runs, 2));
        }

        int indent = line.Length - line.TrimStart().Length;
        string content = line.TrimStart();
        string pad = new(' ', indent);

        int orderedMarker = OrderedListMarkerLength(content);
        if (orderedMarker > 0)
        {
            string number = content[..(orderedMarker - 1)].Trim();
            (string plain, List<MarkdownRun> runs) = ParseInline(content[orderedMarker..]);
            string text = pad + number + ". " + plain;
            List<MarkdownRun> shifted = ShiftRuns(runs, pad.Length + number.Length + 2);
            shifted.Insert(0, new MarkdownRun(pad.Length, number.Length + 1, MarkdownEmphasis.ListMarker));
            return new MarkdownDisplayLine(text, MarkdownBlockStyle.None, shifted);
        }

        if (content.StartsWith("- ", StringComparison.Ordinal) ||
            content.StartsWith("* ", StringComparison.Ordinal) ||
            content.StartsWith("+ ", StringComparison.Ordinal))
        {
            string bullet = indent >= 2 ? "◦" : "•";
            (string plain, List<MarkdownRun> runs) = ParseInline(content[2..]);
            string text = pad + bullet + " " + plain;
            List<MarkdownRun> shifted = ShiftRuns(runs, pad.Length + bullet.Length + 1);
            shifted.Insert(0, new MarkdownRun(pad.Length, bullet.Length, MarkdownEmphasis.ListMarker));
            return new MarkdownDisplayLine(text, MarkdownBlockStyle.None, shifted);
        }

        return ToDisplayLine(line, MarkdownBlockStyle.None);
    }

    private static MarkdownDisplayLine ToDisplayLine(string text, MarkdownBlockStyle blockStyle)
    {
        (string plain, List<MarkdownRun> runs) = ParseInline(text);
        return new MarkdownDisplayLine(plain, blockStyle, runs);
    }

    private static List<MarkdownRun> ShiftRuns(IReadOnlyList<MarkdownRun> runs, int offset)
    {
        if (offset == 0)
        {
            return [.. runs];
        }

        List<MarkdownRun> shifted = new(runs.Count);
        foreach (MarkdownRun run in runs)
        {
            shifted.Add(run with { Start = run.Start + offset });
        }

        return shifted;
    }

    private static IEnumerable<(string Text, IReadOnlyList<MarkdownRun> Runs)> WordWrapRuns(
        string text,
        IReadOnlyList<MarkdownRun> runs,
        int width)
    {
        int offset = 0;
        string remaining = text;
        while (remaining.Length > 0)
        {
            if (remaining.Length <= width)
            {
                yield return (remaining, SliceRuns(runs, offset, remaining.Length));
                yield break;
            }

            int breakAt = width;
            ReadOnlySpan<char> slice = remaining.AsSpan(0, width);
            int lastSpace = slice.LastIndexOf(' ');
            if (lastSpace > width / 3)
            {
                breakAt = lastSpace;
            }

            string chunk = remaining[..breakAt];
            yield return (chunk, SliceRuns(runs, offset, breakAt));

            int consumed = breakAt;
            while (consumed < text.Length && text[consumed] == ' ')
            {
                consumed++;
            }

            offset += consumed;
            remaining = text[offset..];
        }
    }

    private static IReadOnlyList<MarkdownRun> SliceRuns(
        IReadOnlyList<MarkdownRun> runs,
        int start,
        int length)
    {
        if (runs.Count == 0 || length == 0)
        {
            return [];
        }

        int end = start + length;
        List<MarkdownRun> sliced = [];
        foreach (MarkdownRun run in runs)
        {
            int runStart = run.Start;
            int runEnd = run.Start + run.Length;
            if (runEnd <= start || runStart >= end)
            {
                continue;
            }

            int sliceStart = Math.Max(runStart, start) - start;
            int sliceEnd = Math.Min(runEnd, end) - start;
            sliced.Add(new MarkdownRun(sliceStart, sliceEnd - sliceStart, run.Emphasis));
        }

        return sliced;
    }

    private static (string Plain, List<MarkdownRun> Runs) ParseInline(string text)
    {
        StringBuilder plain = new(text.Length);
        List<MarkdownRun> runs = [];
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    int start = plain.Length;
                    string code = text[(i + 1)..end];
                    plain.Append(code);
                    runs.Add(new MarkdownRun(start, code.Length, MarkdownEmphasis.InlineCode));
                    i = end + 1;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                string delim = new(c, 2);
                int end = text.IndexOf(delim, i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    int start = plain.Length;
                    string inner = text[(i + 2)..end];
                    plain.Append(inner);
                    runs.Add(new MarkdownRun(start, inner.Length, MarkdownEmphasis.Bold));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '*' || c == '_')
            {
                int end = text.IndexOf(c, i + 1);
                if (end > i && end != i + 1)
                {
                    int start = plain.Length;
                    string inner = text[(i + 1)..end];
                    plain.Append(inner);
                    runs.Add(new MarkdownRun(start, inner.Length, MarkdownEmphasis.Italic));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                int end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    int start = plain.Length;
                    string inner = text[(i + 2)..end];
                    plain.Append(inner);
                    runs.Add(new MarkdownRun(start, inner.Length, MarkdownEmphasis.Strikethrough));
                    i = end + 2;
                    continue;
                }
            }

            if (c == '[')
            {
                int closeText = text.IndexOf(']', i + 1);
                if (closeText > i && closeText + 1 < text.Length && text[closeText + 1] == '(')
                {
                    int closeUrl = text.IndexOf(')', closeText + 2);
                    if (closeUrl > closeText)
                    {
                        int start = plain.Length;
                        string label = text[(i + 1)..closeText];
                        plain.Append(label);
                        runs.Add(new MarkdownRun(start, label.Length, MarkdownEmphasis.Link));
                        i = closeUrl + 1;
                        continue;
                    }
                }
            }

            plain.Append(c);
            i++;
        }

        return (plain.ToString(), runs);
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

        return trimmed.Count(ch => ch == first) >= 3;
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
}
