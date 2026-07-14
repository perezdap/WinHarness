namespace WinHarness.Cli.Rendering;

/// <summary>
/// Formats the fixed-footer chat prompt as one or more wrapped display lines.
/// Soft-wraps at word boundaries when possible so typing past the edge continues
/// on the next row (the controller grows the fixed footer upward for those rows).
/// Hard newlines in the buffer are preserved as line breaks. A single token longer
/// than the row width is hard-broken mid-word as a last resort.
/// </summary>
internal static class PromptLineView
{
    /// <summary>Glyph and space written at the start of the first prompt row.</summary>
    public const string Prefix = "› ";

    /// <summary>
    /// Soft-wraps <paramref name="buffer"/> into display rows of at most
    /// <paramref name="width"/> characters. The first row is prefixed with
    /// <see cref="Prefix"/>; continuation rows are unprefixed. Never returns an
    /// empty list when <paramref name="width"/> &gt; 0 — an empty buffer yields a
    /// single prefix-only row.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string buffer, int width)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (width <= 0)
        {
            return [];
        }

        // Degenerate: not enough room for the full prefix — clip it.
        if (Prefix.Length >= width)
        {
            return [Prefix[..width]];
        }

        List<string> lines = [];
        int firstContentWidth = width - Prefix.Length;
        string remaining = buffer;

        // First row carries the prompt glyph.
        string firstChunk = Take(remaining, firstContentWidth, out remaining);
        lines.Add(Prefix + firstChunk);

        while (remaining.Length > 0)
        {
            string chunk = Take(remaining, width, out remaining);
            lines.Add(chunk);
        }

        return lines;
    }

    /// <summary>
    /// Number of terminal rows needed to display <paramref name="buffer"/> at
    /// <paramref name="width"/>, always at least 1 when width is positive.
    /// </summary>
    public static int LineCount(string buffer, int width) => Wrap(buffer, width).Count;

    /// <summary>
    /// Takes up to <paramref name="maxChars"/> from the start of
    /// <paramref name="source"/>, preferring a break at the last whitespace in
    /// the window. Stops early at a hard newline (consumed, not included). Falls
    /// back to a mid-word hard break when no whitespace is available.
    /// </summary>
    private static string Take(string source, int maxChars, out string rest)
    {
        if (source.Length == 0)
        {
            rest = string.Empty;
            return string.Empty;
        }

        int limit = Math.Min(maxChars, source.Length);
        for (int i = 0; i < limit; i++)
        {
            if (source[i] is '\n')
            {
                rest = source[(i + 1)..];
                return source[..i];
            }

            // Treat CR as a hard break too (CRLF → blank then continue).
            if (source[i] is '\r')
            {
                int next = i + 1 < source.Length && source[i + 1] == '\n' ? i + 2 : i + 1;
                rest = source[next..];
                return source[..i];
            }
        }

        if (source.Length <= maxChars)
        {
            rest = string.Empty;
            return source;
        }

        // Prefer the last whitespace so whole words move to the next row.
        int breakAt = -1;
        for (int i = maxChars - 1; i >= 1; i--)
        {
            if (char.IsWhiteSpace(source[i]) && source[i] is not ('\n' or '\r'))
            {
                breakAt = i;
                break;
            }
        }

        if (breakAt > 0)
        {
            rest = source[(breakAt + 1)..].TrimStart(' ', '\t');
            return source[..breakAt];
        }

        // No usable whitespace in the window — hard-break mid-word.
        rest = source[maxChars..];
        return source[..maxChars];
    }
}
