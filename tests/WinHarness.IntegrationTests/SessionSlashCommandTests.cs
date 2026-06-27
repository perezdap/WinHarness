using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class SessionSlashCommandTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessSessionSlash", Guid.NewGuid().ToString("N"));
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
    public async Task ForkCreatesNewFileWithCopiedMessages()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        string cwd = Environment.CurrentDirectory;

        ISessionManager source = await factory.CreateAsync(cwd, CancellationToken.None);
        await source.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "first"),
                ConversationMessage.FromText(ConversationRole.Assistant, "second"),
                ConversationMessage.FromText(ConversationRole.User, "third"),
            ],
            CancellationToken.None);

        SessionForkService forkService = new(factory);
        ForkResult forked = await forkService.ForkAsync(source, cwd, CancellationToken.None);

        Assert.IsFalse(string.Equals(source.SessionFilePath, forked.FilePath, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(forked.FilePath));

        SessionFile reopened = await store.OpenAsync(forked.FilePath, CancellationToken.None);
        Assert.AreEqual(3, reopened.Entries.Count);
        Assert.AreEqual("first", ((MessageSessionEntry)reopened.Entries[0]).Message.Text);
        Assert.AreEqual("second", ((MessageSessionEntry)reopened.Entries[1]).Message.Text);
        Assert.AreEqual("third", ((MessageSessionEntry)reopened.Entries[2]).Message.Text);

        ChatSession session = new(source, "local", "coder", renderMarkdown: false);
        session.ReplaceSessionManager(forked.SessionManager);
        session.SyncConversationFromSession();

        Assert.AreEqual(3, session.Conversation.Messages.Count);
        Assert.AreEqual("third", session.Conversation.Messages[^1].Text);
    }

    [TestMethod]
    public async Task CompactAppendsCompactionEntryAndReducesActiveContext()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        string cwd = Environment.CurrentDirectory;

        ISessionManager session = await factory.CreateAsync(cwd, CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, new string('x', 500))],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, new string('y', 500))],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "keep-one")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "keep-two")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "keep-three")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "keep-four")],
            CancellationToken.None);

        int charsBefore = CountChars(session.BuildConversation(skillSystemPrompt: null));

        SessionCompactionService compactionService = new(new FakeSummarizationRuntime("Earlier work summary."));
        CompactionResult result = await compactionService.CompactAsync(
            session,
            "local",
            "coder",
            instructions: "Focus on decisions.",
            skillSystemPrompt: null,
            cwd,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        StringAssert.Contains(result.Message!, "Compaction complete");

        IReadOnlyList<SessionEntry> branch = session.GetActiveBranch();
        Assert.IsInstanceOfType(branch[^1], typeof(CompactionSessionEntry));
        CompactionSessionEntry compaction = (CompactionSessionEntry)branch[^1];
        Assert.AreEqual("Earlier work summary.", compaction.Summary);

        Conversation.Conversation rebuilt = session.BuildConversation(skillSystemPrompt: null);
        Assert.AreEqual(1, rebuilt.Messages.Count(static message => message.Role == ConversationRole.System));
        Assert.AreEqual("keep-one", rebuilt.Messages[1].Text);
        Assert.AreEqual("keep-two", rebuilt.Messages[2].Text);
        Assert.AreEqual("keep-three", rebuilt.Messages[3].Text);
        Assert.AreEqual("keep-four", rebuilt.Messages[4].Text);

        int charsAfter = CountChars(rebuilt);
        Assert.IsTrue(charsAfter < charsBefore);
    }

    [TestMethod]
    public async Task CompactRefusesWhenFewerThanFourMessageEntries()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        ISessionManager session = await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "one"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two"),
                ConversationMessage.FromText(ConversationRole.User, "three"),
            ],
            CancellationToken.None);

        SessionCompactionService compactionService = new(new FakeSummarizationRuntime("unused"));
        CompactionResult result = await compactionService.CompactAsync(
            session,
            "local",
            "coder",
            instructions: null,
            skillSystemPrompt: null,
            Environment.CurrentDirectory,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message!, "Nothing to compact");
    }

    [TestMethod]
    public void TreePickerBranchesToSelectedEntry()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);
        string rootId = session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "root")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "child")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "grandchild")],
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        ChatSession chatSession = new(session, "local", "coder", renderMarkdown: false);
        chatSession.SyncConversationFromSession();
        Assert.AreEqual(3, chatSession.Conversation.Messages.Count);

        IReadOnlyList<string> messages = SessionTreePicker.PickAndBranch(
            chatSession.SessionManager,
            entryId =>
            {
                chatSession.SessionManager.BranchTo(entryId);
                chatSession.SyncConversationFromSession();
            },
            readLine: () => "1");

        Assert.IsTrue(messages.Any(static message => message.StartsWith("Branched to entry", StringComparison.Ordinal)));
        Assert.AreEqual(1, chatSession.Conversation.Messages.Count);
        Assert.AreEqual("root", chatSession.Conversation.Messages[0].Text);
    }

    private static int CountChars(Conversation.Conversation conversation)
    {
        int total = 0;
        foreach (ConversationMessage message in conversation.Messages)
        {
            total += message.Text.Length;
        }

        return total;
    }

    private sealed class FakeSummarizationRuntime : IAgentRuntime
    {
        private readonly string _summary;

        public FakeSummarizationRuntime(string summary)
        {
            _summary = summary;
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentEvent(
                AgentEventKind.Completed,
                "done",
                new TurnArtifacts(
                [
                    ConversationMessage.FromText(ConversationRole.Assistant, _summary),
                ]));
            await Task.CompletedTask;
        }
    }
}