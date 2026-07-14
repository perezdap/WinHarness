using Spectre.Console;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Streams raw assistant tokens to the console as they arrive, emitting a label
/// glyph before the first token of a segment. Every write is cursor-relative
/// (no absolute positioning, no screen erase), so output scrolls naturally and
/// stays inside a DECSTBM scroll region when <see cref="ScreenRegionController"/>
/// has one active — the fixed header/footer rows are never touched.
/// </summary>
internal sealed class AssistantStreamWriter
{
    private bool _labelWritten;

    /// <summary>
    /// Gets a value indicating whether any assistant text has been written for
    /// the current segment (used to decide whether to terminate the line).
    /// </summary>
    public bool HasOutput => _labelWritten;

    /// <summary>
    /// Writes streamed assistant text at the cursor, prefixing the segment label
    /// on the first call.
    /// </summary>
    public void Write(string text)
    {
        if (!_labelWritten)
        {
            AnsiConsole.Markup("[bold blue]•[/] ");
            _labelWritten = true;
        }

        Console.Write(text);
    }
}
