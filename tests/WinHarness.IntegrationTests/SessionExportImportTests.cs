using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class SessionExportImportTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "WinHarnessExport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<ISessionManager> CreateSessionAsync()
    {
        JsonlSessionStore store = new(Path.Combine(_root, "sessions"));
        SessionManagerFactory factory = new(store);
        ISessionManager session = await factory.CreateAsync(_root, CancellationToken.None);
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "hello <script>"),
                new ConversationMessage(ConversationRole.Assistant,
                [
                    ContentBlock.CreateText("hi there"),
                    ContentBlock.CreateToolCall("call_1", "read_file", """{"path":"a.txt"}"""),
                ]),
                new ConversationMessage(ConversationRole.Tool,
                [
                    ContentBlock.CreateToolResult("call_1", "read_file", "file contents", isError: false),
                ]),
            ],
            CancellationToken.None);
        return session;
    }

    [TestMethod]
    public async Task HtmlExportIsSelfContainedAndEscaped()
    {
        ISessionManager session = await CreateSessionAsync();
        string output = Path.Combine(_root, "out.html");

        string written = SessionExportService.Export(session, output);
        string html = File.ReadAllText(written);

        StringAssert.Contains(html, "<!DOCTYPE html>");
        StringAssert.Contains(html, "hello &lt;script&gt;");
        StringAssert.Contains(html, "<details><summary>tool call: read_file");
        StringAssert.Contains(html, "file contents");
        Assert.IsFalse(html.Contains("<script>", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task JsonlExportRoundTripsThroughImportValidation()
    {
        ISessionManager session = await CreateSessionAsync();
        string output = Path.Combine(_root, "out.jsonl");

        SessionExportService.Export(session, output);
        IReadOnlyList<SessionEntry> entries = SessionExportService.ValidateImportFile(output);

        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual("hello <script>", ((MessageSessionEntry)entries[0]).Message.Text);
    }

    [TestMethod]
    public void ImportRejectsMalformedLines()
    {
        string path = Path.Combine(_root, "bad.jsonl");
        File.WriteAllText(path, "{not json}\n");

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionExportService.ValidateImportFile(path));
        StringAssert.Contains(exception.Message, "line 1");
    }

    [TestMethod]
    public void ImportRejectsUnknownParentReferences()
    {
        string path = Path.Combine(_root, "orphan.jsonl");
        File.WriteAllText(
            path,
            """{"type":"message","id":"b","parentId":"missing","timestamp":"2026-01-01T00:00:00Z","message":{"role":1,"content":[{"kind":0,"text":"x"}]}}""" + "\n");

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionExportService.ValidateImportFile(path));
        StringAssert.Contains(exception.Message, "unknown parent");
    }

    [TestMethod]
    public void ImportRejectsEmptyFiles()
    {
        string path = Path.Combine(_root, "empty.jsonl");
        File.WriteAllText(path, "\n\n");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionExportService.ValidateImportFile(path));
    }

    [TestMethod]
    public void ImportRejectsMissingFile()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionExportService.ValidateImportFile(Path.Combine(_root, "absent.jsonl")));
    }
}
