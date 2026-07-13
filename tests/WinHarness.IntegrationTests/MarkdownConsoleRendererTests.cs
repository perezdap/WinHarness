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
}
