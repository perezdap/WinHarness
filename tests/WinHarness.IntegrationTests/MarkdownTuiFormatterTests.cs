using WinHarness.Cli.Rendering;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class MarkdownTuiFormatterTests
{
    [TestMethod]
    public void ParseLines_StripsHeadingMarkers()
    {
        IReadOnlyList<MarkdownDisplayLine> lines = MarkdownTuiFormatter.ParseLines("## Root Cause");

        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("Root Cause", lines[0].Text);
        Assert.AreEqual(MarkdownBlockStyle.Heading2, lines[0].BlockStyle);
    }

    [TestMethod]
    public void ParseLines_PreservesParagraphBreaks()
    {
        IReadOnlyList<MarkdownDisplayLine> lines = MarkdownTuiFormatter.ParseLines("first\n\nsecond");

        Assert.AreEqual(3, lines.Count);
        Assert.AreEqual("first", lines[0].Text);
        Assert.AreEqual(string.Empty, lines[1].Text);
        Assert.AreEqual("second", lines[2].Text);
    }

    [TestMethod]
    public void ParseLines_ParsesInlineCodeAndBold()
    {
        IReadOnlyList<MarkdownDisplayLine> lines = MarkdownTuiFormatter.ParseLines("Use **zero** and `lib.dll`");

        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("Use zero and lib.dll", lines[0].Text);
        Assert.AreEqual(2, lines[0].Runs.Count);
        Assert.AreEqual(MarkdownEmphasis.Bold, lines[0].Runs[0].Emphasis);
        Assert.AreEqual("zero", lines[0].Text.Substring(lines[0].Runs[0].Start, lines[0].Runs[0].Length));
        Assert.AreEqual(MarkdownEmphasis.InlineCode, lines[0].Runs[1].Emphasis);
        Assert.AreEqual("lib.dll", lines[0].Text.Substring(lines[0].Runs[1].Start, lines[0].Runs[1].Length));
    }

    [TestMethod]
    public void ParseLines_ParsesCodeFenceWithoutMarkers()
    {
        IReadOnlyList<MarkdownDisplayLine> lines = MarkdownTuiFormatter.ParseLines("```powershell\nGet-ChildItem\n```");

        Assert.AreEqual(2, lines.Count);
        Assert.AreEqual("powershell", lines[0].Text);
        Assert.AreEqual(MarkdownBlockStyle.CodeFence, lines[0].BlockStyle);
        Assert.AreEqual("Get-ChildItem", lines[1].Text);
        Assert.AreEqual(MarkdownBlockStyle.CodeFence, lines[1].BlockStyle);
    }

    [TestMethod]
    public void WordWrap_AdjustsRunsWhenBreakingLines()
    {
        MarkdownDisplayLine line = new("abcdefghijklmnop", MarkdownBlockStyle.None, [new MarkdownRun(10, 4, MarkdownEmphasis.Bold)]);
        List<(string Text, MarkdownBlockStyle BlockStyle, IReadOnlyList<MarkdownRun> Runs)> wrapped =
            [.. MarkdownTuiFormatter.WordWrap(line, 10)];

        Assert.AreEqual(2, wrapped.Count);
        Assert.AreEqual("abcdefghij", wrapped[0].Text);
        Assert.AreEqual(0, wrapped[0].Runs.Count);
        Assert.AreEqual("klmnop", wrapped[1].Text);
        Assert.AreEqual(1, wrapped[1].Runs.Count);
        Assert.AreEqual(0, wrapped[1].Runs[0].Start);
        Assert.AreEqual(4, wrapped[1].Runs[0].Length);
        Assert.AreEqual(MarkdownEmphasis.Bold, wrapped[1].Runs[0].Emphasis);
    }
}
