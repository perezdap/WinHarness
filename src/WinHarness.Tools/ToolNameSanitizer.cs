namespace WinHarness.Tools;

/// <summary>
/// Produces provider-safe, model-facing tool names. Shared so that collision
/// detection and the adapter agree on the exact name each tool exposes.
/// </summary>
internal static class ToolNameSanitizer
{
    /// <summary>
    /// Replaces characters that some providers (Anthropic/Bedrock) reject in
    /// tool names and ensures the result starts with a valid character.
    /// </summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "tool";
        }

        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        int written = 0;
        foreach (char ch in name)
        {
            buffer[written++] = char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_';
        }

        string sanitized = new(buffer[..written]);
        return char.IsAsciiLetterOrDigit(sanitized[0]) || sanitized[0] is '_'
            ? sanitized
            : "tool_" + sanitized;
    }
}
