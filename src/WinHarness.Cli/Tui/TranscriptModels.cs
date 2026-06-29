using WinHarness.Cli.Rendering;

namespace WinHarness.Cli.Tui;

/// <summary>A logical transcript message before wrapping for display.</summary>
internal sealed record TranscriptMessage(TranscriptRole Role, string Text);

/// <summary>A wrapped display line tagged with the role that produced it.</summary>
internal sealed record TranscriptRow(
    TranscriptRole Role,
    string Text,
    TranscriptRowKind Kind = TranscriptRowKind.Content,
    MarkdownBlockStyle BlockStyle = MarkdownBlockStyle.None,
    IReadOnlyList<MarkdownRun>? Runs = null);

internal static class TranscriptContent
{
    internal const string Indent = "  ";

    public static bool HasDisplayableContent(TranscriptMessage message)
        => !string.IsNullOrWhiteSpace(message.Text);

    public static IEnumerable<string> WordWrap(string text, int width)
    {
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        string remaining = text;
        while (remaining.Length > 0)
        {
            if (remaining.Length <= width)
            {
                yield return remaining;
                yield break;
            }

            int breakAt = width;
            ReadOnlySpan<char> slice = remaining.AsSpan(0, width);
            int lastNewline = slice.LastIndexOf('\n');
            int lastSpace = slice.LastIndexOf(' ');
            if (lastNewline >= 0)
            {
                breakAt = lastNewline + 1;
            }
            else if (lastSpace > width / 3)
            {
                breakAt = lastSpace;
            }

            yield return remaining[..breakAt].TrimEnd('\n');
            remaining = remaining[breakAt..].TrimStart('\n').TrimStart(' ');
        }
    }
}
