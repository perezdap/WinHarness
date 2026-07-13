using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class UsageFooterTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessUsageFooter", Guid.NewGuid().ToString("N"));
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
    public void CompactRendersHumanReadableCounts()
    {
        Assert.AreEqual("950", UsageFooter.Compact(950));
        Assert.AreEqual("30.4k", UsageFooter.Compact(30_400));
        Assert.AreEqual("1.0m", UsageFooter.Compact(1_000_000));
        Assert.AreEqual("0", UsageFooter.Compact(0));
    }

    [TestMethod]
    public async Task SumsAssistantUsageAcrossActiveBranch()
    {
        ChatSession session = await CreateSessionAsync();
        await AppendAssistantWithUsageAsync(session, input: 100, output: 40);
        await AppendAssistantWithUsageAsync(session, input: 250, output: 60);
        session.SyncConversationFromSession();

        (long input, long output) = UsageFooter.SumSessionUsage(session);

        Assert.AreEqual(350, input);
        Assert.AreEqual(100, output);
    }

    [TestMethod]
    public async Task FindLastTurnUsageReturnsMostRecent()
    {
        ChatSession session = await CreateSessionAsync();
        await AppendAssistantWithUsageAsync(session, input: 100, output: 40);
        await AppendAssistantWithUsageAsync(session, input: 250, output: 60);

        MessageUsage? usage = UsageFooter.FindLastTurnUsage(session);

        Assert.IsNotNull(usage);
        Assert.AreEqual(250, usage.InputTokens);
        Assert.AreEqual(60, usage.OutputTokens);
    }

    [TestMethod]
    public async Task FormatIncludesModelContextEffortAndTotals()
    {
        WinHarnessOptions options = new() { DefaultProvider = "local", DefaultModel = "coder" };
        ProviderOptions provider = new() { Id = "local", Kind = "openai-compatible" };
        provider.Models.Add(new ModelOptions { Id = "coder", ProviderModelId = "m", ContextWindow = 10_000 });
        options.Providers.Add(provider);

        ChatSession session = await CreateSessionAsync();
        session.ReasoningEffort = "high";
        await AppendAssistantWithUsageAsync(session, input: 500, output: 200);
        session.SyncConversationFromSession();

        string footer = UsageFooter.Format(session, options, UsageFooter.FindLastTurnUsage(session));

        StringAssert.Contains(footer, "coder @ local");
        StringAssert.Contains(footer, "effort high");
        StringAssert.Contains(footer, "ctx ~");
        StringAssert.Contains(footer, "turn ↑500 ↓200");
        StringAssert.Contains(footer, "session ↑500 ↓200");
    }

    [TestMethod]
    public async Task StatusLineResolvesModelDefaultEffortWhenSessionOverrideUnset()
    {
        WinHarnessOptions options = new() { DefaultProvider = "local", DefaultModel = "coder" };
        ProviderOptions provider = new() { Id = "local", Kind = "openai-compatible" };
        provider.Models.Add(new ModelOptions { Id = "coder", ProviderModelId = "m", ReasoningEffort = "medium" });
        options.Providers.Add(provider);
        ChatSession session = await CreateSessionAsync();

        string effort = StatusLineFormatter.ResolveEffort(session, options);

        Assert.AreEqual("medium", effort);
    }

    private async Task<ChatSession> CreateSessionAsync()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        ISessionManager sessionManager = await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        return new ChatSession(sessionManager, "local", "coder", renderMarkdown: false);
    }

    private static async Task AppendAssistantWithUsageAsync(ChatSession session, long input, long output)
    {
        ConversationMessage assistant = ConversationMessage.FromText(ConversationRole.Assistant, "answer") with
        {
            Usage = new MessageUsage(input, output, input + output)
        };
        await session.SessionManager.AppendMessagesAsync(
            [ConversationMessage.FromText(ConversationRole.User, "ask"), assistant],
            CancellationToken.None);
    }
}
