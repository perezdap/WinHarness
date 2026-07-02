using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class AutoCompactionTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessAutoCompact", Guid.NewGuid().ToString("N"));
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
    public async Task ProactiveCompactionTriggersNearContextWindow()
    {
        WinHarnessOptions options = CreateOptions(contextWindow: 1000, reserveTokens: 500);
        ChatSession session = await CreatePersistedSessionAsync(messageChars: 4000);
        CountingRuntime runtime = new("summary of earlier work");

        // 8 messages x 4000 chars / 4 chars-per-token = ~8000 tokens > 1000 - 500.
        string? notice = await AutoCompactionService.TryProactiveCompactAsync(
            options,
            session,
            runtime,
            CancellationToken.None);

        Assert.IsNotNull(notice);
        StringAssert.Contains(notice, "[auto-compact]");
        Assert.AreEqual(1, runtime.Runs);
        Assert.IsTrue(session.SessionManager.GetActiveBranch().Any(static entry => entry is CompactionSessionEntry));
    }

    [TestMethod]
    public async Task ProactiveCompactionSkipsWhenUnderThreshold()
    {
        // 8 small messages stay far below 100_000 - 4096 tokens.
        WinHarnessOptions options = CreateOptions(contextWindow: 100_000, reserveTokens: 4096);
        ChatSession session = await CreatePersistedSessionAsync(messageChars: 20);
        CountingRuntime runtime = new("unused");

        string? notice = await AutoCompactionService.TryProactiveCompactAsync(
            options,
            session,
            runtime,
            CancellationToken.None);

        Assert.IsNull(notice);
        Assert.AreEqual(0, runtime.Runs);
    }

    [TestMethod]
    public async Task ProactiveCompactionSkipsWhenDisabled()
    {
        WinHarnessOptions options = CreateOptions(contextWindow: 1000, reserveTokens: 500);
        options.Compaction.AutoCompact = false;
        ChatSession session = await CreatePersistedSessionAsync(messageChars: 4000);
        CountingRuntime runtime = new("unused");

        string? notice = await AutoCompactionService.TryProactiveCompactAsync(
            options,
            session,
            runtime,
            CancellationToken.None);

        Assert.IsNull(notice);
        Assert.AreEqual(0, runtime.Runs);
    }

    [TestMethod]
    public async Task ProactiveCompactionSkipsEphemeralSessions()
    {
        WinHarnessOptions options = CreateOptions(contextWindow: 1000, reserveTokens: 500);
        ChatSession session = new("local", "coder", renderMarkdown: false);
        CountingRuntime runtime = new("unused");

        string? notice = await AutoCompactionService.TryProactiveCompactAsync(
            options,
            session,
            runtime,
            CancellationToken.None);

        Assert.IsNull(notice);
        Assert.AreEqual(0, runtime.Runs);
    }

    [TestMethod]
    public async Task ReactiveCompactionRunsRegardlessOfEstimate()
    {
        // Estimate is under threshold, but the provider reported overflow.
        WinHarnessOptions options = CreateOptions(contextWindow: 100_000, reserveTokens: 4096);
        ChatSession session = await CreatePersistedSessionAsync(messageChars: 20);
        CountingRuntime runtime = new("summary");

        string? notice = await AutoCompactionService.TryReactiveCompactAsync(
            options,
            session,
            runtime,
            CancellationToken.None);

        Assert.IsNotNull(notice);
        StringAssert.Contains(notice, "Retrying turn");
        Assert.AreEqual(1, runtime.Runs);
    }

    [TestMethod]
    public void ContextOverflowDetectionMatchesCommonProviderMessages()
    {
        Assert.IsTrue(AutoCompactionService.IsContextOverflow(
            "This model's maximum context length is 8192 tokens."));
        Assert.IsTrue(AutoCompactionService.IsContextOverflow(
            "context_length_exceeded: reduce the length of the messages"));
        Assert.IsTrue(AutoCompactionService.IsContextOverflow(
            "Requested tokens exceed the context window of this model"));
        Assert.IsTrue(AutoCompactionService.IsContextOverflow(
            "Please reduce the length of your prompt tokens."));

        Assert.IsFalse(AutoCompactionService.IsContextOverflow(null));
        Assert.IsFalse(AutoCompactionService.IsContextOverflow(""));
        Assert.IsFalse(AutoCompactionService.IsContextOverflow("rate limit exceeded"));
        Assert.IsFalse(AutoCompactionService.IsContextOverflow("invalid api key"));
    }

    [TestMethod]
    public void ResolveContextWindowFallsBackWhenUnset()
    {
        WinHarnessOptions options = CreateOptions(contextWindow: null, reserveTokens: 4096);

        int resolved = AutoCompactionService.ResolveContextWindow(options, "local", "coder");

        Assert.AreEqual(AutoCompactionService.DefaultContextWindow, resolved);
    }

    [TestMethod]
    public void ResolveContextWindowUsesConfiguredValue()
    {
        WinHarnessOptions options = CreateOptions(contextWindow: 32_000, reserveTokens: 4096);

        int resolved = AutoCompactionService.ResolveContextWindow(options, "local", "coder");

        Assert.AreEqual(32_000, resolved);
    }

    private static WinHarnessOptions CreateOptions(int? contextWindow, int reserveTokens)
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "coder"
        };
        ProviderOptions provider = new()
        {
            Id = "local",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1"
        };
        provider.Models.Add(new ModelOptions
        {
            Id = "coder",
            ProviderModelId = "test-model",
            ContextWindow = contextWindow
        });
        options.Providers.Add(provider);
        options.Compaction.ReserveTokens = reserveTokens;
        return options;
    }

    private async Task<ChatSession> CreatePersistedSessionAsync(int messageChars)
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        ISessionManager sessionManager = await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);

        for (int index = 0; index < 4; index++)
        {
            await sessionManager.AppendMessagesAsync(
                [
                    ConversationMessage.FromText(ConversationRole.User, new string('u', messageChars)),
                    ConversationMessage.FromText(ConversationRole.Assistant, new string('a', messageChars)),
                ],
                CancellationToken.None);
        }

        ChatSession session = new(sessionManager, "local", "coder", renderMarkdown: false);
        session.SyncConversationFromSession();
        return session;
    }

    private sealed class CountingRuntime : IAgentRuntime
    {
        private readonly string _summary;

        public CountingRuntime(string summary)
        {
            _summary = summary;
        }

        public int Runs { get; private set; }

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Runs++;
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
