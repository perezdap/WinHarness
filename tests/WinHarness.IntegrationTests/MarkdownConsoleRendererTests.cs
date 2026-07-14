using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Spectre.Console;
using Spectre.Console.Testing;
using WinHarness.Cli.Rendering;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class MarkdownConsoleRendererTests
{
    private static readonly Regex AnsiEscapeSequence = new(
        @"\u001B(?:\[[0-?]*[ -/]*[@-~]|\][^\u0007]*(?:\u0007|\u001B\\))",
        RegexOptions.Compiled);

    private static string StripAnsi(string value) => AnsiEscapeSequence.Replace(value, string.Empty);

    private static string RenderToPlain(string markdown)
    {
        TestConsole console = new TestConsole().Width(80);
        AnsiConsole.Console = console;
        MarkdownConsoleRenderer.Write(markdown);
        return console.Output;
    }

    [TestMethod]
    public void Table_RendersHeaderAndRowsAsSpectreTable()
    {
        string markdown =
            """
            | Provider | pi/ | winharness/ |
            |----------|-----|-------------|
            | atlas-cloud | yes | yes |
            | zai-coding | yes | yes |
            """;

        string plain = StripAnsi(RenderToPlain(markdown));

        // Header cells appear.
        StringAssert.Contains(plain, "Provider");
        StringAssert.Contains(plain, "pi/");
        StringAssert.Contains(plain, "winharness/");

        // Data cells appear.
        StringAssert.Contains(plain, "atlas-cloud");
        StringAssert.Contains(plain, "zai-coding");

        // A Spectre rounded table draws top/bottom borders using '╭' / '╰'.
        // Either corner presence confirms a table was rendered (not a literal
        // pipe row). Some terminals/captures emit '╮'/'╯'; accept any corner.
        bool hasTableBorder = plain.Contains('╭') || plain.Contains('╮') ||
                              plain.Contains('╰') || plain.Contains('╯') ||
                              plain.Contains('─');
        Assert.IsTrue(hasTableBorder, $"Expected a Spectre table border. Output:{Environment.NewLine}{plain}");

        // The raw pipe-delimited separator line must NOT pass through verbatim.
        Assert.IsFalse(plain.Contains("|----------|"), "Separator row leaked verbatim.");
    }

    [TestMethod]
    public void Table_FollowedByParagraph_OnlyConsumesTableRows()
    {
        string markdown =
            """
            | a | b |
            |---|---|
            | 1 | 2 |

            After table line.
            """;

        string plain = StripAnsi(RenderToPlain(markdown));

        StringAssert.Contains(plain, "After table line.");
        Assert.IsFalse(plain.Contains("|---|---|"), "Separator row leaked verbatim.");
    }

    [TestMethod]
    public void Prose_With_Pipe_IsNotTreatedAsTable()
    {
        // A single pipe in prose with no following separator stays a normal line.
        string markdown = "Use the pipe | operator here." + Environment.NewLine + "Next line is normal.";

        string plain = StripAnsi(RenderToPlain(markdown));

        StringAssert.Contains(plain, "Use the pipe");
        StringAssert.Contains(plain, "Next line is normal.");
        Assert.IsFalse(plain.Contains('╭') || plain.Contains('╰'),
            "A Spectre table border should not appear for pipe-less prose.");
    }

    [TestMethod]
    public void Link_WithTitle_DoesNotCrashAndUsesDestination()
    {
        // A markdown link with an optional title: [text](url "title"). The
        // title's quotes must not leak into Spectre's [link=...] markup (which
        // previously threw "Could not find color or style '"title'").
        string markdown = "[Search](https://example.com/search \"Search results\")";

        string plain = StripAnsi(RenderToPlain(markdown));

        // Does not crash (the regression) and renders the link label.
        StringAssert.Contains(plain, "Search");
        // The title text should not leak into the rendered output.
        Assert.IsFalse(plain.Contains("Search results", StringComparison.Ordinal),
            "Link title must not leak into rendered output.");
    }

    [TestMethod]
    public void Html_DetailsSummaryBlock_TagsStrippedContentKept()
    {
        // A model-emitted collapsible block must not leak <details>/<summary>
        // as literal text; the inner markdown (list + code fence) stays.
        string markdown =
            """
            <details>
            <summary><strong>Click to expand</strong></summary>

            - item one
            - item two

            ```rust
            fn emoji() -> &'static str { "\u{1F98A}" }
            ```
            </details>
            """;

        string plain = StripAnsi(RenderToPlain(markdown));

        Assert.IsFalse(plain.Contains("<details>", StringComparison.Ordinal), "<details> leaked.");
        Assert.IsFalse(plain.Contains("</details>", StringComparison.Ordinal), "</details> leaked.");
        Assert.IsFalse(plain.Contains("<summary>", StringComparison.Ordinal), "<summary> leaked.");
        Assert.IsFalse(plain.Contains("<strong>", StringComparison.Ordinal), "<strong> leaked.");
        Assert.IsTrue(plain.Contains("Click to expand", StringComparison.Ordinal), "Summary text dropped.");
        Assert.IsTrue(plain.Contains("item one", StringComparison.Ordinal) && plain.Contains("item two", StringComparison.Ordinal),
            "Inner list items dropped.");
        Assert.IsTrue(plain.Contains("fn emoji", StringComparison.Ordinal), "Inner code fence dropped.");
    }

    [TestMethod]
    public void InlineHtml_TagsStrippedFromProse()
    {
        // Inline <strong>/<em>/<code> in flowing prose must not leak as literal tags.
        string markdown = "Use <strong>bold</strong> and <em>italic</em> and <code>code</code> here.";

        string plain = StripAnsi(RenderToPlain(markdown));

        Assert.IsFalse(plain.Contains('<'), $"Raw HTML tag leaked into: {plain}");
        Assert.IsTrue(plain.Contains("bold") && plain.Contains("italic") && plain.Contains("code"),
            "Inner text of inline tags was dropped.");
    }

    [TestMethod]
    public void Prose_WithLessThan_NotTreatedAsHtml()
    {
        // Math/prose with '<' followed by a non-letter must not be stripped.
        string markdown = "count < 10 and x <= 3";

        string plain = StripAnsi(RenderToPlain(markdown));

        StringAssert.Contains(plain, "count");
        StringAssert.Contains(plain, "10");
    }
}
