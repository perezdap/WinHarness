using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ChatSessionBootstrapTests
{
    private string _sessionsRoot = null!;
    private string _workspace = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessBootstrap", Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(Path.GetTempPath(), "WinHarnessBootstrap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_sessionsRoot))
        {
            Directory.Delete(_sessionsRoot, recursive: true);
        }

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [TestMethod]
    public async Task InteractiveDefaultContinuesMostRecentSession()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _workspace;
            JsonlSessionStore store = new(_sessionsRoot);
            SessionManagerFactory factory = new(store);

            ISessionManager older = await factory.CreateAsync(_workspace, CancellationToken.None);
            await older.AppendMessagesAsync(
                [ConversationMessage.FromText(ConversationRole.User, "older")],
                CancellationToken.None);
            File.SetLastWriteTimeUtc(older.SessionFilePath!, DateTime.UtcNow.AddMinutes(-5));

            ISessionManager newer = await factory.CreateAsync(_workspace, CancellationToken.None);
            await newer.AppendMessagesAsync(
                [ConversationMessage.FromText(ConversationRole.User, "newer")],
                CancellationToken.None);

            ISessionManager resolved = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: false,
                    NoSession: false,
                    ContinueSession: false,
                    Resume: false,
                    Session: null,
                    Name: null),
                CancellationToken.None);

            Assert.AreEqual(newer.SessionFilePath, resolved.SessionFilePath);
            Assert.AreEqual("newer", resolved.BuildConversation(null).Messages[0].Text);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public async Task NoSessionUsesInMemoryManager()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _workspace;
            JsonlSessionStore store = new(_sessionsRoot);
            SessionManagerFactory factory = new(store);

            ISessionManager resolved = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: false,
                    NoSession: true,
                    ContinueSession: false,
                    Resume: false,
                    Session: null,
                    Name: null),
                CancellationToken.None);

            Assert.IsFalse(resolved.IsPersisted);
            Assert.IsNull(resolved.SessionFilePath);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public async Task OneShotDefaultIsEphemeralUnlessContinueRequested()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _workspace;
            JsonlSessionStore store = new(_sessionsRoot);
            SessionManagerFactory factory = new(store);

            ISessionManager ephemeral = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: true,
                    NoSession: false,
                    ContinueSession: false,
                    Resume: false,
                    Session: null,
                    Name: null),
                CancellationToken.None);
            Assert.IsFalse(ephemeral.IsPersisted);

            ISessionManager persisted = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: true,
                    NoSession: false,
                    ContinueSession: true,
                    Resume: false,
                    Session: null,
                    Name: null),
                CancellationToken.None);
            Assert.IsTrue(persisted.IsPersisted);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public async Task NameAppliedWhenCreatingFirstSession()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _workspace;
            JsonlSessionStore store = new(_sessionsRoot);
            SessionManagerFactory factory = new(store);

            ISessionManager resolved = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: false,
                    NoSession: false,
                    ContinueSession: false,
                    Resume: false,
                    Session: null,
                    Name: "Phase 1 bootstrap"),
                CancellationToken.None);

            Assert.AreEqual("Phase 1 bootstrap", resolved.DisplayName);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [TestMethod]
    public async Task OpenBySessionIdSuffix()
    {
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _workspace;
            JsonlSessionStore store = new(_sessionsRoot);
            SessionManagerFactory factory = new(store);

            ISessionManager created = await factory.CreateAsync(_workspace, CancellationToken.None);
            await created.AppendMessagesAsync(
                [ConversationMessage.FromText(ConversationRole.User, "target")],
                CancellationToken.None);

            IReadOnlyList<SessionSummary> summaries = await factory.ListAsync(_workspace, CancellationToken.None);
            Assert.AreEqual(1, summaries.Count);

            ISessionManager resolved = await ChatSessionBootstrap.ResolveAsync(
                factory,
                new ChatSessionBootstrapRequest(
                    IsOneShot: false,
                    NoSession: false,
                    ContinueSession: false,
                    Resume: false,
                    Session: summaries[0].SessionId,
                    Name: null),
                CancellationToken.None);

            Assert.AreEqual(created.SessionFilePath, resolved.SessionFilePath);
            Assert.AreEqual("target", resolved.BuildConversation(null).Messages[0].Text);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }
}