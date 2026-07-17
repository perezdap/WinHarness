using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ActiveBranchTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessActiveBranch", Guid.NewGuid().ToString("N"));
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
    public async Task LastOfTypeReturnsNullOnEmptyBranch()
    {
        ISessionManager session = await CreateSessionAsync();

        Assert.IsNull(ActiveBranch.Load(session).LastOfType<ModelChangeSessionEntry>());
    }

    [TestMethod]
    public async Task LastOfTypeReturnsNullWhenKindAbsent()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "one"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two"),
            ],
            CancellationToken.None);

        Assert.IsNull(ActiveBranch.Load(session).LastOfType<ModelChangeSessionEntry>());
    }

    [TestMethod]
    public async Task LastOfTypeReturnsMostRecentMatch()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendModelChangeAsync("local", "first-model", CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "between")],
            CancellationToken.None);
        await session.AppendModelChangeAsync("local", "second-model", CancellationToken.None);

        ModelChangeSessionEntry? found = ActiveBranch.Load(session).LastOfType<ModelChangeSessionEntry>();

        Assert.IsNotNull(found);
        Assert.AreEqual("second-model", found.ModelId);
    }

    [TestMethod]
    public async Task LastOfTypeWithPredicateSkipsNonMatchingEntries()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.Assistant, "with usage") with
                {
                    Usage = new MessageUsage(10, 5, 15)
                },
                ConversationMessage.FromText(ConversationRole.Assistant, "without usage"),
            ],
            CancellationToken.None);

        MessageSessionEntry? found = ActiveBranch.Load(session)
            .LastOfType<MessageSessionEntry>(static entry => entry.Message.Usage is not null);

        Assert.IsNotNull(found);
        Assert.AreEqual("with usage", found.Message.Text);
    }

    [TestMethod]
    public async Task CountMessageEntriesCountsOnlyMessages()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "one"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two"),
            ],
            CancellationToken.None);
        await session.AppendModelChangeAsync("local", "coder", CancellationToken.None);
        await session.AppendSessionInfoAsync("named", CancellationToken.None);

        Assert.AreEqual(2, ActiveBranch.Load(session).CountMessageEntries());
    }

    [TestMethod]
    public async Task QueriesReflectBranchedState()
    {
        ISessionManager session = await CreateSessionAsync();
        string rootId = await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "root")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.Assistant, "child")],
            CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "grandchild")],
            CancellationToken.None);

        session.BranchTo(rootId);
        ActiveBranch branch = ActiveBranch.Load(session);

        Assert.AreEqual(1, branch.CountMessageEntries());
        Assert.AreEqual("root", branch.FlattenMessages()[0].Text);
    }

    [TestMethod]
    public async Task SumAssistantUsageSumsAssistantMessagesOnly()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "ask"),
                ConversationMessage.FromText(ConversationRole.Assistant, "one") with
                {
                    Usage = new MessageUsage(100, 40, 140)
                },
                ConversationMessage.FromText(ConversationRole.Assistant, "no usage"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two") with
                {
                    Usage = new MessageUsage(250, 60, 310)
                },
            ],
            CancellationToken.None);

        (long input, long output) = ActiveBranch.Load(session).SumAssistantUsage();

        Assert.AreEqual(350, input);
        Assert.AreEqual(100, output);
    }

    [TestMethod]
    public async Task SumAssistantUsageTreatsUnsetTokenCountsAsZero()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.Assistant, "partial") with
                {
                    Usage = new MessageUsage(InputTokens: 100)
                },
            ],
            CancellationToken.None);

        (long input, long output) = ActiveBranch.Load(session).SumAssistantUsage();

        Assert.AreEqual(100, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public async Task FlattenMessagesPreservesBranchOrderAndContent()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "first"),
                ConversationMessage.FromText(ConversationRole.Assistant, "second"),
            ],
            CancellationToken.None);
        await session.AppendModelChangeAsync("local", "coder", CancellationToken.None);
        await session.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "third")],
            CancellationToken.None);

        List<ConversationMessage> messages = ActiveBranch.Load(session).FlattenMessages();

        Assert.AreEqual(3, messages.Count);
        Assert.AreEqual(ConversationRole.User, messages[0].Role);
        Assert.AreEqual("first", messages[0].Text);
        Assert.AreEqual(ConversationRole.Assistant, messages[1].Role);
        Assert.AreEqual("second", messages[1].Text);
        Assert.AreEqual(ConversationRole.User, messages[2].Role);
        Assert.AreEqual("third", messages[2].Text);
    }

    [TestMethod]
    public async Task FromEntriesFlattensArbitraryEntryLists()
    {
        ISessionManager session = await CreateSessionAsync();
        await session.AppendMessagesAsync(
            [
                ConversationMessage.FromText(ConversationRole.User, "one"),
                ConversationMessage.FromText(ConversationRole.Assistant, "two"),
            ],
            CancellationToken.None);
        IReadOnlyList<SessionEntry> entries = session.GetActiveBranch();

        List<ConversationMessage> messages = ActiveBranch.FromEntries(entries).FlattenMessages();

        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual("two", messages[1].Text);
    }

    [TestMethod]
    public void SumMessageTextCharsTotalsTextLength()
    {
        ConversationState conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, new string('x', 10)));
        conversation.Add(ConversationMessage.FromText(ConversationRole.Assistant, new string('y', 25)));

        Assert.AreEqual(35, ActiveBranch.SumMessageTextChars(conversation));
    }

    private async Task<ISessionManager> CreateSessionAsync()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        return await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
    }
}
