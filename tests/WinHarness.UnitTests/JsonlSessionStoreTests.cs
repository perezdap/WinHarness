using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Serialization;
using WinHarness.Sessions;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class JsonlSessionStoreTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessSessions", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_sessionsRoot))
        {
            Directory.Delete(_sessionsRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RoundTripsAllSessionEntryTypes()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        string cwd = @"C:\Users\dperez\Documents\Github\WinHarness";

        SessionFile created = await store.CreateAsync(cwd, CancellationToken.None);
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-06-27T14:00:02.000Z");

        MessageSessionEntry messageEntry = new(
            "b2c3d4e5",
            "a1b2c3d4",
            timestamp,
            new ConversationMessage(
                ConversationRole.Assistant,
                [
                    ContentBlock.CreateText("I'll read the file."),
                    ContentBlock.CreateToolCall("call_01", "read_file", """{"path":"README.md"}""")
                ],
                ProviderId: "openai-main",
                ModelId: "gpt-primary",
                Usage: new MessageUsage(1200, 340, 1540)));

        CompactionSessionEntry compactionEntry = new(
            "f6g7h8i9",
            "e5f6g7h8",
            timestamp.AddMinutes(10),
            "User asked to add session persistence.",
            "c3d4e5f6",
            42000);

        ModelChangeSessionEntry modelChangeEntry = new(
            "d4e5f6g7",
            "c3d4e5f6",
            timestamp.AddMinutes(5),
            "local-ollama",
            "local-coder");

        SessionInfoSessionEntry sessionInfoEntry = new(
            "k1l2m3n4",
            "j0k1l2m3",
            timestamp.AddMinutes(35),
            "Phase 1 sessions design");

        await store.AppendAsync(created.Path, messageEntry, CancellationToken.None);
        await store.AppendAsync(created.Path, compactionEntry, CancellationToken.None);
        await store.AppendAsync(created.Path, modelChangeEntry, CancellationToken.None);
        await store.AppendAsync(created.Path, sessionInfoEntry, CancellationToken.None);

        SessionFile opened = await store.OpenAsync(created.Path, CancellationToken.None);

        Assert.AreEqual(4, opened.Entries.Count);
        Assert.IsInstanceOfType(opened.Entries[0], typeof(MessageSessionEntry));
        Assert.IsInstanceOfType(opened.Entries[1], typeof(CompactionSessionEntry));
        Assert.IsInstanceOfType(opened.Entries[2], typeof(ModelChangeSessionEntry));
        Assert.IsInstanceOfType(opened.Entries[3], typeof(SessionInfoSessionEntry));

        MessageSessionEntry roundTrippedMessage = (MessageSessionEntry)opened.Entries[0];
        Assert.AreEqual("b2c3d4e5", roundTrippedMessage.Id);
        Assert.AreEqual("read_file", roundTrippedMessage.Message.Content[1].ToolName);
        Assert.AreEqual(1540, roundTrippedMessage.Message.Usage?.TotalTokens);

        CompactionSessionEntry roundTrippedCompaction = (CompactionSessionEntry)opened.Entries[1];
        Assert.AreEqual("c3d4e5f6", roundTrippedCompaction.FirstKeptEntryId);
        Assert.AreEqual(42000, roundTrippedCompaction.TokensBefore);

        string raw = await File.ReadAllTextAsync(created.Path);
        StringAssert.Contains(raw, "\"type\":\"message\"");
        StringAssert.Contains(raw, "\"type\":\"compaction\"");
        StringAssert.Contains(raw, "\"type\":\"model_change\"");
        StringAssert.Contains(raw, "\"type\":\"session_info\"");
    }

    [TestMethod]
    public async Task CreateWritesHeaderAndUsesWorkspaceKeyPath()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        string cwd = @"C:\Users\dperez\Documents\Github\WinHarness";

        SessionFile created = await store.CreateAsync(cwd, CancellationToken.None);

        StringAssert.StartsWith(created.Path, Path.Combine(_sessionsRoot, "c-users-dperez-documents-github-winharness"));
        StringAssert.EndsWith(created.Path, ".jsonl");
        Assert.AreEqual(0, created.Entries.Count);
        Assert.AreEqual(Path.GetFullPath(cwd), created.Header.Cwd);
        Assert.AreEqual(SessionHeader.EntryType, created.Header.Type);
        Assert.AreEqual(1, created.Header.Version);

        string firstLine = (await File.ReadAllLinesAsync(created.Path))[0];
        SessionHeader? header = JsonSerializer.Deserialize(firstLine, WinHarnessJsonSerializerContext.Default.SessionHeader);
        Assert.IsNotNull(header);
        Assert.AreEqual("session", header.Type);
    }

    [TestMethod]
    public async Task AppendUsesAppendOnlyWrites()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionFile created = await store.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        byte[] bytesBefore = await File.ReadAllBytesAsync(created.Path);

        MessageSessionEntry entry = new(
            SessionEntryIds.Create(),
            null,
            DateTimeOffset.UtcNow,
            ConversationMessage.FromText(ConversationRole.User, "hello"));

        await store.AppendAsync(created.Path, entry, CancellationToken.None);
        byte[] bytesAfterFirstAppend = await File.ReadAllBytesAsync(created.Path);
        await store.AppendAsync(created.Path, entry with { Id = SessionEntryIds.Create(), ParentId = entry.Id }, CancellationToken.None);
        byte[] bytesAfterSecondAppend = await File.ReadAllBytesAsync(created.Path);

        CollectionAssert.AreEqual(bytesBefore, bytesAfterFirstAppend.AsSpan(0, bytesBefore.Length).ToArray());
        CollectionAssert.AreEqual(bytesAfterFirstAppend, bytesAfterSecondAppend.AsSpan(0, bytesAfterFirstAppend.Length).ToArray());
        Assert.IsTrue(bytesAfterSecondAppend.Length > bytesAfterFirstAppend.Length);
    }

    [TestMethod]
    public async Task ListReturnsSummariesOrderedByLastModified()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        string cwd = Environment.CurrentDirectory;

        SessionFile first = await store.CreateAsync(cwd, CancellationToken.None);
        await store.AppendAsync(
            first.Path,
            new MessageSessionEntry(
                SessionEntryIds.Create(),
                null,
                DateTimeOffset.UtcNow,
                ConversationMessage.FromText(ConversationRole.User, "first preview")),
            CancellationToken.None);
        await store.AppendAsync(
            first.Path,
            new SessionInfoSessionEntry(
                SessionEntryIds.Create(),
                null,
                DateTimeOffset.UtcNow,
                "Older session"),
            CancellationToken.None);
        File.SetLastWriteTimeUtc(first.Path, DateTime.UtcNow.AddMinutes(-5));

        SessionFile second = await store.CreateAsync(cwd, CancellationToken.None);
        await store.AppendAsync(
            second.Path,
            new MessageSessionEntry(
                SessionEntryIds.Create(),
                null,
                DateTimeOffset.UtcNow,
                ConversationMessage.FromText(ConversationRole.User, "second preview")),
            CancellationToken.None);
        await store.AppendAsync(
            second.Path,
            new MessageSessionEntry(
                SessionEntryIds.Create(),
                null,
                DateTimeOffset.UtcNow,
                ConversationMessage.FromText(ConversationRole.Assistant, "reply")),
            CancellationToken.None);
        await store.AppendAsync(
            second.Path,
            new SessionInfoSessionEntry(
                SessionEntryIds.Create(),
                null,
                DateTimeOffset.UtcNow,
                "Newer session"),
            CancellationToken.None);

        IReadOnlyList<SessionSummary> summaries = await store.ListAsync(cwd, CancellationToken.None);

        Assert.AreEqual(2, summaries.Count);
        Assert.AreEqual("Newer session", summaries[0].DisplayName);
        Assert.AreEqual("second preview", summaries[0].FirstUserPreview);
        Assert.AreEqual(2, summaries[0].MessageCount);
        Assert.AreEqual("Older session", summaries[1].DisplayName);
        Assert.AreEqual("first preview", summaries[1].FirstUserPreview);
        Assert.AreEqual(1, summaries[1].MessageCount);
        StringAssert.EndsWith(summaries[0].FilePath, ".jsonl");
    }

    [TestMethod]
    public async Task ParentIdChainsAcrossAppendedEntries()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionFile created = await store.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        string rootId = SessionEntryIds.Create();
        MessageSessionEntry root = new(
            rootId,
            null,
            timestamp,
            ConversationMessage.FromText(ConversationRole.User, "root"));

        string childId = SessionEntryIds.Create();
        MessageSessionEntry child = new(
            childId,
            rootId,
            timestamp.AddSeconds(1),
            ConversationMessage.FromText(ConversationRole.Assistant, "child"));

        string grandchildId = SessionEntryIds.Create();
        MessageSessionEntry grandchild = new(
            grandchildId,
            childId,
            timestamp.AddSeconds(2),
            ConversationMessage.FromText(ConversationRole.User, "grandchild"));

        await store.AppendAsync(created.Path, root, CancellationToken.None);
        await store.AppendAsync(created.Path, child, CancellationToken.None);
        await store.AppendAsync(created.Path, grandchild, CancellationToken.None);

        SessionFile opened = await store.OpenAsync(created.Path, CancellationToken.None);

        Assert.AreEqual(3, opened.Entries.Count);
        Assert.IsNull(((MessageSessionEntry)opened.Entries[0]).ParentId);
        Assert.AreEqual(rootId, ((MessageSessionEntry)opened.Entries[1]).ParentId);
        Assert.AreEqual(childId, ((MessageSessionEntry)opened.Entries[2]).ParentId);
    }

    [TestMethod]
    public void SerializesSessionDtosWithSourceGeneratedContext()
    {
        SessionHeader header = new(
            "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            DateTimeOffset.Parse("2026-06-27T14:00:00.000Z"),
            @"C:\repo",
            null);

        MessageSessionEntry messageEntry = new(
            "b2c3d4e5",
            "a1b2c3d4",
            DateTimeOffset.Parse("2026-06-27T14:00:02.000Z"),
            ConversationMessage.FromText(ConversationRole.User, "hello"));

        string headerJson = JsonSerializer.Serialize(header, WinHarnessJsonSerializerContext.Default.SessionHeader);
        string messageJson = JsonSerializer.Serialize(
            (SessionEntry)messageEntry,
            WinHarnessJsonSerializerContext.Default.SessionEntry);

        StringAssert.Contains(headerJson, "\"type\":\"session\"");
        StringAssert.Contains(messageJson, "\"type\":\"message\"");
        StringAssert.Contains(messageJson, "\"parentId\":\"a1b2c3d4\"");
    }

    [TestMethod]
    public async Task DeleteAsyncRemovesFileOrTrashes()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionFile created = await store.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        Assert.IsTrue(File.Exists(created.Path));

        // Soft delete (trash)
        SessionDeletionResult softResult = await store.DeleteAsync(created.Path, permanent: false, CancellationToken.None);
        Assert.AreEqual(SessionDeletionStatus.Trashed, softResult.Status);
        Assert.IsFalse(File.Exists(created.Path));
        Assert.IsTrue(File.Exists(softResult.FinalPath));
        Assert.IsTrue(softResult.FinalPath!.Contains(".trash"));

        // Permanent delete
        SessionDeletionResult permResult = await store.DeleteAsync(softResult.FinalPath, permanent: true, CancellationToken.None);
        Assert.AreEqual(SessionDeletionStatus.PermanentlyDeleted, permResult.Status);
        Assert.IsFalse(File.Exists(softResult.FinalPath));

        // Not found deletion
        SessionDeletionResult nfResult = await store.DeleteAsync(created.Path, permanent: false, CancellationToken.None);
        Assert.AreEqual(SessionDeletionStatus.NotFound, nfResult.Status);
    }

    [TestMethod]
    public async Task ListAllAsyncListsAcrossWorkspaces()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        string cwd1 = Path.Combine(Environment.CurrentDirectory, "ws1");
        string cwd2 = Path.Combine(Environment.CurrentDirectory, "ws2");

        SessionFile ws1File = await store.CreateAsync(cwd1, CancellationToken.None);
        SessionFile ws2File = await store.CreateAsync(cwd2, CancellationToken.None);

        IReadOnlyList<SessionSummary> all = await store.ListAllAsync(CancellationToken.None);

        // Verify we find both
        Assert.IsTrue(all.Any(s => s.FilePath == ws1File.Path));
        Assert.IsTrue(all.Any(s => s.FilePath == ws2File.Path));
    }
}