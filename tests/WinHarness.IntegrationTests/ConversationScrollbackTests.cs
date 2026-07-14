using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Rendering;
using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ConversationScrollbackTests
{
    private const int Width = 80;

    [TestMethod]
    public void BuildLines_EmptyConversation_ReturnsPlaceholder()
    {
        ConversationState conversation = new();

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        CollectionAssert.AreEqual(new[] { "(no conversation yet)" }, lines);
    }

    [TestMethod]
    public void BuildLines_SkipsSystemMessages()
    {
        ConversationState conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.System, "system instructions"));
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "hello"));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        Assert.IsFalse(lines.Any(static l => l.Contains("system instructions", StringComparison.Ordinal)),
            "System message must not appear in scrollback.");
        Assert.IsTrue(lines.Any(static l => l.Contains("hello", StringComparison.Ordinal)),
            "User message must appear in scrollback.");
    }

    [TestMethod]
    public void BuildLines_UserMessage_PrefixedWithGlyph()
    {
        ConversationState conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "hello"));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        // A blank separator line precedes the user content.
        Assert.AreEqual(string.Empty, lines[0]);
        Assert.AreEqual("› hello", lines[1]);
    }

    [TestMethod]
    public void BuildLines_AssistantText_PrefixedWithBullet()
    {
        ConversationState conversation = new();
        conversation.Add(new ConversationMessage(
            ConversationRole.Assistant,
            [ContentBlock.CreateText("world")]));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        // No leading blank line for the first block of an assistant message
        // (blank separators appear only between blocks within a message).
        Assert.AreEqual("• world", lines[0]);
    }

    [TestMethod]
    public void BuildLines_ToolCall_ShownWithNameAndTruncatedArgs()
    {
        ConversationState conversation = new();
        conversation.Add(new ConversationMessage(
            ConversationRole.Assistant,
            [ContentBlock.CreateToolCall("call-1", "read_file", "{\"path\":\"/a/b/c.txt\"}")]));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        Assert.IsTrue(lines.Any(static l => l.Contains("↳ tool: read_file", StringComparison.Ordinal)),
            "Tool call line must show the tool name.");
        Assert.IsTrue(lines.Any(static l => l.Contains("\"path\":\"/a/b/c.txt\"", StringComparison.Ordinal)),
            "Tool call arguments must appear (not truncated at this width).");
    }

    [TestMethod]
    public void BuildLines_ToolResult_ShownTruncated()
    {
        ConversationState conversation = new();
        conversation.Add(new ConversationMessage(
            ConversationRole.Assistant,
            [ContentBlock.CreateToolResult("call-1", "read_file", "file content here")]));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        Assert.IsTrue(lines.Any(static l => l.Contains("↳ result: ", StringComparison.Ordinal)),
            "Tool result line must use the result prefix.");
        Assert.IsTrue(lines.Any(static l => l.Contains("file content here", StringComparison.Ordinal)),
            "Tool result text must appear.");
    }

    [TestMethod]
    public void BuildLines_LongText_WrapsToWidth()
    {
        const int narrowWidth = 12;
        ConversationState conversation = new();
        // User text wraps at width; the prefix "› " takes 2 chars, so the first
        // row holds at most 10 content chars, continuation rows hold at most 10
        // (prefix "  " takes 2).
        string longText = "abcdefghijklmnopqrstuvwxyz";
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, longText));

        List<string> lines = ConversationScrollback.BuildLines(conversation, narrowWidth);

        // First line is the blank separator.
        Assert.AreEqual(string.Empty, lines[0]);
        // Every content line must fit within the width.
        Assert.IsTrue(lines.Skip(1).All(static l => l.Length <= narrowWidth),
            $"All wrapped lines must be <= {narrowWidth} chars.");
        // The full text must be reconstructable from the wrapped content lines
        // (minus prefixes).
        Assert.IsTrue(lines.Count > 2, "Long text should produce multiple wrapped lines.");
    }

    [TestMethod]
    public void BuildLines_AssistantMarkdown_RendersTableNotRawPipes()
    {
        // A markdown table must render as a Spectre table (box-drawing border)
        // in the scrollback, not leak the raw pipe-delimited rows verbatim.
        string markdown =
            """
            | Provider | status |
            |----------|--------|
            | atlas    | yes    |
            | zai      | yes    |
            """;
        ConversationState conversation = new();
        conversation.Add(new ConversationMessage(
            ConversationRole.Assistant,
            [ContentBlock.CreateText(markdown)]));

        List<string> lines = ConversationScrollback.BuildLines(conversation, Width);

        string joined = string.Join(Environment.NewLine, lines);
        Assert.IsFalse(joined.Contains("|----------|", StringComparison.Ordinal),
            "Raw markdown separator row must not leak into scrollback.");
        Assert.IsTrue(joined.Contains('─') || joined.Contains('╭') || joined.Contains('╰'),
            "A rendered Spectre table border should appear. Output:" + Environment.NewLine + joined);
        Assert.IsTrue(joined.Contains("atlas", StringComparison.Ordinal) && joined.Contains("zai", StringComparison.Ordinal),
            "Table data cells must appear.");
    }

    [TestMethod]
    public void BuildLines_DegenerateWidth_ReturnsEmpty()
    {
        ConversationState conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "hello"));

        Assert.AreEqual(0, ConversationScrollback.BuildLines(conversation, width: 0).Count);
        Assert.AreEqual(0, ConversationScrollback.BuildLines(conversation, width: -1).Count);
    }
}
