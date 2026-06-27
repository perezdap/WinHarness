using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class SessionManagerTests
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
    public async Task BranchNavigationChangesActivePath()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);

        string rootId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "root")],
            CancellationToken.None);
        string childId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "child")],
            CancellationToken.None);
        string grandchildId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "grandchild")],
            CancellationToken.None);

        Assert.AreEqual(3, session.GetActiveBranch().Count);
        Assert.AreEqual(grandchildId, session.LeafEntryId);

        session.BranchTo(rootId);

        Assert.AreEqual(rootId, session.LeafEntryId);
        Assert.AreEqual(1, session.GetActiveBranch().Count);
        Assert.AreEqual("root", ((MessageSessionEntry)session.GetActiveBranch()[0]).Message.Text);

        string branchId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "branch")],
            CancellationToken.None);

        Assert.AreEqual(2, session.GetChildren(rootId).Count);
        Assert.AreEqual(branchId, session.LeafEntryId);
        Assert.AreEqual(2, session.GetActiveBranch().Count);
        Assert.AreEqual("branch", ((MessageSessionEntry)session.GetActiveBranch()[1]).Message.Text);

        SessionTree tree = session.GetTree();
        Assert.AreEqual(branchId, tree.LeafEntryId);
        Assert.AreEqual(2, tree.Nodes.Count(static node => node.IsOnActiveBranch));
    }

    [TestMethod]
    public async Task AppendMessagesChainsParentIds()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        ISessionManager session = await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);

        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "one"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two"),
                ConversationMessage.FromText(ConversationRole.User, "three")
            ],
            CancellationToken.None);

        IReadOnlyList<SessionEntry> branch = session.GetActiveBranch();
        Assert.AreEqual(3, branch.Count);
        Assert.IsNull(branch[0].ParentId);
        Assert.AreEqual(branch[0].Id, branch[1].ParentId);
        Assert.AreEqual(branch[1].Id, branch[2].ParentId);
        Assert.AreEqual(branch[2].Id, session.LeafEntryId);

        SessionFile reopened = await store.OpenAsync(session.SessionFilePath!, CancellationToken.None);
        Assert.AreEqual(3, reopened.Entries.Count);
        Assert.IsNull(reopened.Entries[0].ParentId);
        Assert.AreEqual(reopened.Entries[0].Id, reopened.Entries[1].ParentId);
        Assert.AreEqual(reopened.Entries[1].Id, reopened.Entries[2].ParentId);
    }

    [TestMethod]
    public async Task CompactionOverlaySkipsEarlierMessagesAndInjectsSummary()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);

        string firstId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "drop-me")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "also-drop")],
            CancellationToken.None);
        string keptUserId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "keep-me")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "keep-too")],
            CancellationToken.None);

        await session.AppendCompactionAsync(
            "Summary of earlier work.",
            keptUserId,
            tokensBefore: 9000,
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "after-compact")],
            CancellationToken.None);

        ConversationState conversation = session.BuildConversation(skillSystemPrompt: null);

        Assert.AreEqual(4, conversation.Messages.Count);
        Assert.AreEqual(ConversationRole.System, conversation.Messages[0].Role);
        Assert.AreEqual("Summary of earlier work.", conversation.Messages[0].Text);
        Assert.AreEqual("keep-me", conversation.Messages[1].Text);
        Assert.AreEqual("keep-too", conversation.Messages[2].Text);
        Assert.AreEqual("after-compact", conversation.Messages[3].Text);
        Assert.IsFalse(conversation.Messages.Any(static message => message.Text == "drop-me"));
        Assert.IsFalse(conversation.Messages.Any(static message => message.Text == "also-drop"));
        Assert.AreNotEqual(firstId, keptUserId);
    }

    [TestMethod]
    public async Task ContinueRecentAsyncOpensLatestSessionForWorkspace()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        string cwd = Environment.CurrentDirectory;

        ISessionManager older = await factory.CreateAsync(cwd, CancellationToken.None);
        await older.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "older")],
            CancellationToken.None);
        File.SetLastWriteTimeUtc(older.SessionFilePath!, DateTime.UtcNow.AddMinutes(-10));

        ISessionManager newer = await factory.CreateAsync(cwd, CancellationToken.None);
        await newer.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "newer")],
            CancellationToken.None);
        await newer.AppendSessionInfoAsync("Latest session", CancellationToken.None);

        ISessionManager continued = await factory.ContinueRecentAsync(cwd, CancellationToken.None);

        Assert.AreEqual(newer.SessionFilePath, continued.SessionFilePath);
        Assert.AreEqual("Latest session", continued.DisplayName);
        Assert.AreEqual("newer", continued.BuildConversation(null).Messages[0].Text);
    }

    [TestMethod]
    public async Task ContinueRecentAsyncCreatesNewSessionWhenNoneExist()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        string cwd = Environment.CurrentDirectory;

        ISessionManager session = await factory.ContinueRecentAsync(cwd, CancellationToken.None);

        Assert.IsTrue(session.IsPersisted);
        Assert.IsNotNull(session.SessionFilePath);
        Assert.IsTrue(File.Exists(session.SessionFilePath));
        Assert.AreEqual(0, session.GetActiveBranch().Count);
    }

    [TestMethod]
    public async Task ModelChangeDoesNotAppearInBuildConversationOutput()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);

        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "hello")],
            CancellationToken.None);
        await session.AppendModelChangeAsync("local-ollama", "local-coder", CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "world")],
            CancellationToken.None);

        ConversationState conversation = session.BuildConversation(skillSystemPrompt: null);

        Assert.AreEqual(2, conversation.Messages.Count);
        Assert.AreEqual("hello", conversation.Messages[0].Text);
        Assert.AreEqual("world", conversation.Messages[1].Text);
        Assert.IsFalse(conversation.Messages.Any(static message =>
            message.ProviderId == "local-ollama" || message.ModelId == "local-coder"));
    }

    [TestMethod]
    public async Task BuildConversationPrependsSkillSystemPrompt()
    {
        ISessionManager session = SessionManager.InMemory(Environment.CurrentDirectory);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "task")],
            CancellationToken.None);

        ConversationState conversation = session.BuildConversation("Use the refactor skill.");

        Assert.AreEqual(2, conversation.Messages.Count);
        Assert.AreEqual(ConversationRole.System, conversation.Messages[0].Role);
        Assert.AreEqual("Use the refactor skill.", conversation.Messages[0].Text);
        Assert.AreEqual("task", conversation.Messages[1].Text);
    }
}