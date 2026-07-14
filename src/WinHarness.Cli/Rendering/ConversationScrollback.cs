using System.Text;
using Spectre.Console;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Rendering;

/// <summary>
/// Full-screen pager that re-renders the persisted conversation into a
/// navigable view, opened via PageUp at the idle prompt when the DECSTBM
/// scroll region is active. Assistant text blocks are rendered through
/// <see cref="MarkdownConsoleRenderer"/> (captured off-screen, ANSI styling
/// stripped) so tables, code fences, and lists keep their structure instead of
/// showing raw markdown. On close, restores the chat viewport so the DECSC/DECRC
/// cursor-save slot holds a fresh empty line at the bottom of the region.
/// </summary>
internal static class ConversationScrollback
{
    /// <summary>
    /// Builds display lines for the entire conversation, skipping System-role
    /// messages. Pure with respect to console state: the markdown capture swaps
    /// <see cref="AnsiConsole.Console"/> only around each assistant text block
    /// and restores it before returning. Returns an empty list when
    /// <paramref name="width"/> is non-positive, and a single placeholder when
    /// the conversation has no displayable messages. All returned lines fit
    /// within <paramref name="width"/> visible columns.
    /// </summary>
    internal static List<string> BuildLines(ConversationState conversation, int width)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        List<string> lines = [];
        if (width <= 0)
        {
            return lines;
        }

        foreach (ConversationMessage message in conversation.Messages)
        {
            if (message.Role == ConversationRole.System)
            {
                continue;
            }

            if (message.Role == ConversationRole.User)
            {
                lines.Add(string.Empty);
                AppendPrefixed(lines, message.Text, width, "› ", "  ");
                continue;
            }

            if (message.Role == ConversationRole.Assistant)
            {
                bool firstBlock = true;
                foreach (ContentBlock block in message.Content)
                {
                    switch (block.Kind)
                    {
                        case ContentBlockKind.Text:
                            if (!firstBlock)
                            {
                                lines.Add(string.Empty);
                            }

                            AppendAssistantText(lines, block.Text ?? string.Empty, width);
                            firstBlock = false;
                            break;

                        case ContentBlockKind.ToolCall:
                            if (!firstBlock)
                            {
                                lines.Add(string.Empty);
                            }

                            lines.Add(Truncate("  ↳ tool: " + (block.ToolName ?? string.Empty), width));
                            lines.Add("    " + Truncate(block.ArgumentsJson ?? string.Empty, Math.Max(0, width - 4)));
                            firstBlock = false;
                            break;

                        case ContentBlockKind.ToolResult:
                            if (!firstBlock)
                            {
                                lines.Add(string.Empty);
                            }

                            string resultPrefix = block.IsError ? "  ↳ result (error): " : "  ↳ result: ";
                            lines.Add(Truncate(resultPrefix + (block.Text ?? string.Empty), width));
                            firstBlock = false;
                            break;
                    }
                }

                continue;
            }

            // Tool-role messages are represented via ToolResult blocks on the
            // assistant turn; standalone Tool messages are skipped to avoid
            // duplicate rendering.
        }

        if (lines.Count == 0)
        {
            lines.Add("(no conversation yet)");
        }

        return lines;
    }

    /// <summary>
    /// Opens the pager over the current alternate screen buffer. No-op when
    /// <paramref name="screen"/> is inactive or stdin/stdout is redirected. The
    /// caller (<see cref="Program.ReadKeyLine"/>) must have
    /// <see cref="Console.TreatControlCAsInput"/> already set to <c>true</c> and
    /// must repaint the prompt via <c>paint(buffer)</c> after this returns.
    /// </summary>
    internal static void Open(ChatSession session, ScreenRegionController screen)
    {
        if (!screen.IsActive || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return;
        }

        int width = screen.Layout.Width;
        int height = screen.Layout.Height;
        List<string> lines = BuildLines(session.Conversation, width);

        // Row 1 = header bar, last row = footer bar, rows 2..H-1 = viewport.
        int viewportHeight = Math.Max(1, height - 2);
        int maxOffset = Math.Max(0, lines.Count - viewportHeight);
        int offset = maxOffset; // bottom-aligned (most recent at bottom).

        // Reset the DECSTBM region to the full screen and clear. We stay in the
        // same alternate buffer (no ESC[?1049l) so Exit can still restore the
        // caller's prior terminal contents.
        Console.Write("\x1b[r");
        Console.Write("\x1b[2J\x1b[H");

        try
        {
            while (true)
            {
                RenderViewport(lines, offset, viewportHeight, width);

                ConsoleKeyInfo key;
                try
                {
                    key = Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    // stdin closed (EOF) — close the pager.
                    break;
                }

                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Q || IsCtrlC(key))
                {
                    break;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        offset = Math.Max(0, offset - 1);
                        break;
                    case ConsoleKey.DownArrow:
                        offset = Math.Min(maxOffset, offset + 1);
                        break;
                    case ConsoleKey.PageUp:
                        offset = Math.Max(0, offset - viewportHeight);
                        break;
                    case ConsoleKey.PageDown:
                        offset = Math.Min(maxOffset, offset + viewportHeight);
                        break;
                    case ConsoleKey.Home:
                        offset = 0;
                        break;
                    case ConsoleKey.End:
                        offset = maxOffset;
                        break;
                }
            }
        }
        finally
        {
            RestoreChatViewport(screen, lines);
        }
    }

    /// <summary>
    /// Restores the scroll region, repaints chrome, clears the scrolling
    /// region of leftover pager content, writes the tail of the conversation
    /// into it, and DECSCs a fresh empty line at the bottom so
    /// <see cref="ScreenRegionController.EndPrompt"/>'s DECRC lands the next
    /// turn's streaming output on a clean line (no mid-row overlap and no
    /// stale pager glyphs).
    /// </summary>
    private static void RestoreChatViewport(ScreenRegionController screen, List<string> lines)
    {
        int scrollTop = screen.Layout.ScrollTop;
        int scrollBottom = screen.Layout.ScrollBottom;
        int regionHeight = scrollBottom - scrollTop + 1;

        // RestoreViewport re-establishes the DECSTBM region, repaints the fixed
        // header/footer (WriteBar uses its own DECSC/DECRC pair, leaving the
        // saved slot holding the pre-Repaint position), and positions the cursor
        // at the top of the scrolling region.
        screen.RestoreViewport();

        // The pager drew viewport rows across the whole screen; clear every
        // scrolling-region row first so tail shorter than the region leaves no
        // stale glyphs behind. (Header row 1 and footer row H were repainted by
        // RestoreViewport and stay untouched here.)
        for (int row = scrollTop; row <= scrollBottom; row++)
        {
            Console.Write($"\u001b[{row};1H\u001b[2K");
        }

        // Reposition at the top of the region before writing the tail.
        Console.Write($"\u001b[{scrollTop};1H");

        // Show at most regionHeight-1 tail lines: the last WriteLine leaves the
        // cursor on a fresh empty line inside the region (row <= scrollBottom),
        // so no DECSTBM scroll occurs and the next turn streams onto that line
        // instead of overwriting the tail's last row.
        int maxShow = Math.Max(1, regionHeight - 1);
        int start = Math.Max(0, lines.Count - maxShow);
        for (int i = start; i < lines.Count; i++)
        {
            Console.WriteLine(lines[i]);
        }

        // DECSC the fresh-line position so the pending EndPrompt DECRCs here.
        // This overwrites whatever position Repaint's WriteBar left in the slot.
        Console.Write("\u001b7");
    }

    /// <summary>
    /// Renders the pager frame: a blue header bar (row 1), the viewport slice
    /// (rows 2..viewportHeight+1), and a cyan footer bar with the position
    /// indicator and close hint (last row). Every viewport row is cleared before
    /// writing so navigation never leaves stale glyphs.
    /// </summary>
    private static void RenderViewport(List<string> lines, int offset, int viewportHeight, int width)
    {
        // Header bar (row 1, blue background, bright white text).
        Console.Write($"\x1b[1;1H\x1b[2K\x1b[44;97m{PadBar("Conversation History", width)}\x1b[0m");

        // Viewport rows (row 2 onward). Lines are pre-sized to width by
        // BuildLines, so they are written verbatim (no truncation).
        for (int row = 0; row < viewportHeight; row++)
        {
            int idx = offset + row;
            int targetRow = row + 2;
            Console.Write($"\x1b[{targetRow};1H\x1b[2K");
            if (idx < lines.Count)
            {
                Console.Write(lines[idx]);
            }
        }

        // Footer bar (last row, cyan background, bright white text).
        int footerRow = viewportHeight + 2;
        string footer = $"[line {offset + 1} of {lines.Count}]  Esc/q to close";
        Console.Write($"\x1b[{footerRow};1H\x1b[2K\x1b[46;97m{PadBar(footer, width)}\x1b[0m");
    }

    /// <summary>
    /// Renders an assistant text block as markdown via
    /// <see cref="MarkdownConsoleRenderer"/> captured off-screen, strips ANSI
    /// styling (keeping table/panel/list structure as plain box-drawing text),
    /// and prefixes the first line with the assistant bullet.
    /// </summary>
    private static void AppendAssistantText(List<string> lines, string text, int width)
    {
        IReadOnlyList<string> rendered = CaptureMarkdownLines(text, width);
        if (rendered.Count == 0)
        {
            lines.Add("• ");
            return;
        }

        lines.Add("• " + rendered[0]);
        for (int i = 1; i < rendered.Count; i++)
        {
            lines.Add(rendered[i]);
        }
    }

    /// <summary>
    /// Captures <see cref="MarkdownConsoleRenderer.Write"/> output into a
    /// scratch <see cref="IAnsiConsole"/> backed by a <see cref="StringWriter"/>
    /// and splits it into lines, preserving ANSI styling so headings, tables,
    /// code-fence panels, and lists render in the scrollback exactly as they do
    /// in the live chat. Temporarily swaps the global
    /// <see cref="AnsiConsole.Console"/> (the renderer writes to the static
    /// facade) and restores it in <c>finally</c>. Trailing empty lines are
    /// dropped so a markdown block does not leave blank scrollback rows.
    /// </summary>
    private static IReadOnlyList<string> CaptureMarkdownLines(string markdown, int width)
    {
        if (markdown.Length == 0)
        {
            return [];
        }

        StringWriter writer = new();
        IAnsiConsole capture = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.Yes,
        });
        capture.Profile.Width = width;

        IAnsiConsole previous = AnsiConsole.Console;
        AnsiConsole.Console = capture;
        try
        {
            MarkdownConsoleRenderer.Write(markdown);
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        // Keep ANSI styling (Spectre wraps every line to width, so lines fit
        // without truncation). Split on newlines and drop a trailing blank line
        // produced by the renderer's final newline.
        string captured = writer.ToString();
        List<string> result = [];
        string[] split = captured.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < split.Length; i++)
        {
            // Keep interior blank lines (paragraph spacing) but drop a trailing
            // run of blanks produced by the renderer's final newline.
            if (i == split.Length - 1 && split[i].Length == 0)
            {
                continue;
            }

            result.Add(split[i]);
        }

        return result;
    }

    /// <summary>
    /// Appends word-wrapped <paramref name="text"/> to <paramref name="lines"/>,
    /// prefixing the first row with <paramref name="firstPrefix"/> and
    /// continuation rows with <paramref name="contPrefix"/>.
    /// </summary>
    private static void AppendPrefixed(
        List<string> lines, string text, int width, string firstPrefix, string contPrefix)
    {
        // The effective content width for the first row is reduced by the prefix;
        // continuation rows use the full width reduced by their prefix.
        int firstWidth = Math.Max(1, width - firstPrefix.Length);
        int contWidth = Math.Max(1, width - contPrefix.Length);

        IReadOnlyList<string> wrapped = WordWrap(text, firstWidth, contWidth);
        for (int i = 0; i < wrapped.Count; i++)
        {
            lines.Add((i == 0 ? firstPrefix : contPrefix) + wrapped[i]);
        }
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> at word boundaries. The first row is
    /// at most <paramref name="firstWidth"/> characters; subsequent rows are at
    /// most <paramref name="contWidth"/>. A single token longer than the row
    /// width is hard-broken mid-word. Empty text yields a single empty row so the
    /// prefix still renders.
    /// </summary>
    private static IReadOnlyList<string> WordWrap(string text, int firstWidth, int contWidth)
    {
        if (firstWidth <= 0 && contWidth <= 0)
        {
            return [string.Empty];
        }

        List<string> rows = [];

        // Split on existing newlines first so hard breaks are preserved.
        string[] paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        bool isFirstRow = true;
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                rows.Add(string.Empty);
                isFirstRow = false;
                continue;
            }

            ReadOnlySpan<char> remaining = paragraph.AsSpan();
            while (remaining.Length > 0)
            {
                int limit = isFirstRow ? firstWidth : contWidth;
                if (limit <= 0)
                {
                    limit = contWidth > 0 ? contWidth : firstWidth;
                }

                if (limit <= 0)
                {
                    rows.Add(string.Empty);
                    break;
                }

                if (remaining.Length <= limit)
                {
                    rows.Add(remaining.ToString());
                    isFirstRow = false;
                    break;
                }

                // Try to break at the last whitespace within the limit.
                int breakAt = -1;
                for (int i = limit; i > 0; i--)
                {
                    if (char.IsWhiteSpace(remaining[i - 1]))
                    {
                        breakAt = i - 1;
                        break;
                    }
                }

                if (breakAt > 0)
                {
                    rows.Add(remaining[..breakAt].ToString());
                    remaining = remaining[(breakAt + 1)..];
                }
                else
                {
                    // No whitespace found — hard-break at the limit.
                    rows.Add(remaining[..limit].ToString());
                    remaining = remaining[limit..];
                }

                isFirstRow = false;
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(string.Empty);
        }

        return rows;
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to <paramref name="maxLen"/>, appending
    /// an ellipsis when content is clipped. Mirrors
    /// <see cref="ScreenRegionController"/> truncation style.
    /// </summary>
    private static string Truncate(string text, int maxLen)
    {
        if (text.Length <= maxLen)
        {
            return text;
        }

        if (maxLen <= 1)
        {
            return maxLen <= 0 ? string.Empty : text[..maxLen];
        }

        return text[..(maxLen - 1)] + "…";
    }

    /// <summary>
    /// Pads/truncates <paramref name="text"/> to exactly <paramref name="width"/>
    /// for use as a full-width bar (no trailing reset needed — the caller wraps
    /// with SGR reset).
    /// </summary>
    private static string PadBar(string text, int width)
    {
        string body = Truncate(text, width);
        if (body.Length < width)
        {
            body = body.PadRight(width);
        }

        return body;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="key"/> is Ctrl+C. Inlined rather
    /// than sharing <c>Program.IsCtrlC</c> (private) to keep this class surgical.
    /// </summary>
    private static bool IsCtrlC(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0;
}
