using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Cli.Chat;
using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Runtime;
using WinHarness.Sessions;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class TurnPumpTests
{
    private string _sessionsRoot = null!;

    [TestInitialize]
    public void SetUp()
    {
        _sessionsRoot = Path.Combine(Path.GetTempPath(), "WinHarnessTurnPump", Guid.NewGuid().ToString("N"));
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
    public async Task SuccessfulTurnAppendsFullArtifactsOnce()
    {
        ChatSession session = await CreateSessionAsync();
        TurnArtifacts artifacts = new(
        [
            ConversationMessage.FromText(ConversationRole.User, "hi"),
            ConversationMessage.FromText(ConversationRole.Assistant, "hello"),
        ]);
        ScriptedRuntime runtime = new(
            new AgentEvent(AgentEventKind.AssistantDelta, "hello"),
            new AgentEvent(AgentEventKind.Completed, TurnPump.NormalCompletionMessage, artifacts));

        TurnOutcome outcome = await RunPumpAsync(session, runtime);

        Assert.IsNull(outcome.FailureMessage);
        Assert.IsFalse(outcome.AppendedPartialArtifacts);
        Assert.AreSame(artifacts, outcome.AppendedArtifacts);
        CollectionAssert.AreEqual(
            new[] { "hi", "hello" },
            session.Conversation.Messages.Select(static message => message.Text).ToArray());
    }

    [TestMethod]
    public async Task SuccessfulTurnCapturesUsageInOutcome()
    {
        ChatSession session = await CreateSessionAsync();
        MessageUsage usage = new(InputTokens: 12, OutputTokens: 34, TotalTokens: 46);
        TurnArtifacts artifacts = new(
        [
            ConversationMessage.FromText(ConversationRole.User, "hi"),
            ConversationMessage.FromText(ConversationRole.Assistant, "hello") with { Usage = usage },
        ]);
        ScriptedRuntime runtime = new(
            new AgentEvent(AgentEventKind.Completed, TurnPump.NormalCompletionMessage, artifacts));

        TurnOutcome outcome = await RunPumpAsync(session, runtime);

        Assert.AreSame(usage, outcome.Usage);
    }

    [TestMethod]
    public async Task FailedThenPartialCompletionAppendsPartialArtifactsOnce()
    {
        ChatSession session = await CreateSessionAsync();
        TurnArtifacts partial = new(
        [
            ConversationMessage.FromText(ConversationRole.User, "hi"),
            ConversationMessage.FromText(ConversationRole.Assistant, "truncated"),
        ]);
        ScriptedRuntime runtime = new(
            new AgentEvent(AgentEventKind.AssistantDelta, "trunc"),
            new AgentEvent(AgentEventKind.Failed, "boom"),
            new AgentEvent(AgentEventKind.Completed, TurnPump.PartialCompletionMessage, partial));

        List<AgentEventKind> observed = [];
        TurnPump pump = new(session);
        TurnOutcome outcome = await pump.RunAsync(
            CreateRequest(session),
            runtime,
            agentEvent =>
            {
                observed.Add(agentEvent.Kind);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.AreEqual("boom", outcome.FailureMessage);
        Assert.IsTrue(outcome.AppendedPartialArtifacts);
        Assert.AreSame(partial, outcome.AppendedArtifacts);
        CollectionAssert.AreEqual(
            new[] { "hi", "truncated" },
            session.Conversation.Messages.Select(static message => message.Text).ToArray());
        CollectionAssert.AreEqual(
            new[] { AgentEventKind.AssistantDelta, AgentEventKind.Failed, AgentEventKind.Completed },
            observed);
    }

    [TestMethod]
    public async Task FailureWithoutPartialCompletionAppendsNothing()
    {
        ChatSession session = await CreateSessionAsync();
        ScriptedRuntime runtime = new(
            new AgentEvent(AgentEventKind.AssistantDelta, "partial text"),
            new AgentEvent(AgentEventKind.Failed, "provider exploded"));

        TurnOutcome outcome = await RunPumpAsync(session, runtime);

        Assert.AreEqual("provider exploded", outcome.FailureMessage);
        Assert.IsNull(outcome.AppendedArtifacts);
        Assert.IsFalse(outcome.AppendedPartialArtifacts);
        Assert.AreEqual(0, session.Conversation.Messages.Count);
    }

    [TestMethod]
    public async Task PresentObservesEventsInStreamOrder()
    {
        ChatSession session = await CreateSessionAsync();
        ScriptedRuntime runtime = new(
            new AgentEvent(AgentEventKind.AssistantDelta, "a"),
            new AgentEvent(
                AgentEventKind.ToolActivity,
                "tool",
                ToolActivity: new ToolActivityInfo("read_file", ToolActivityPhase.Started)),
            new AgentEvent(AgentEventKind.AssistantDelta, "b"),
            new AgentEvent(
                AgentEventKind.Completed,
                TurnPump.NormalCompletionMessage,
                new TurnArtifacts([ConversationMessage.FromText(ConversationRole.Assistant, "ab")])));

        List<(AgentEventKind Kind, string Message)> observed = [];
        TurnPump pump = new(session);
        await pump.RunAsync(
            CreateRequest(session),
            runtime,
            agentEvent =>
            {
                observed.Add((agentEvent.Kind, agentEvent.Message));
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                (AgentEventKind.AssistantDelta, "a"),
                (AgentEventKind.ToolActivity, "tool"),
                (AgentEventKind.AssistantDelta, "b"),
                (AgentEventKind.Completed, TurnPump.NormalCompletionMessage),
            },
            observed);
    }

    [TestMethod]
    public async Task UnconsumedSteeringIsPromotedToFollowUps()
    {
        ChatSession session = await CreateSessionAsync();
        session.Steering.Enqueue("keep going");
        ScriptedRuntime runtime = new(
            new AgentEvent(
                AgentEventKind.Completed,
                TurnPump.NormalCompletionMessage,
                new TurnArtifacts([ConversationMessage.FromText(ConversationRole.Assistant, "done")])));

        await RunPumpAsync(session, runtime);

        Queue<string> followUps = new();
        TurnPump.PromoteUnconsumedSteering(session.Steering, followUps);

        Assert.AreEqual(0, session.Steering.Count);
        CollectionAssert.AreEqual(new[] { "keep going" }, followUps.ToArray());
    }

    [TestMethod]
    public async Task CancellationPropagatesAndAppendsNothing()
    {
        ChatSession session = await CreateSessionAsync();
        CancellingRuntime runtime = new();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RunPumpAsync(session, runtime));

        Assert.AreEqual(0, session.Conversation.Messages.Count);
    }

    private async Task<ChatSession> CreateSessionAsync()
    {
        JsonlSessionStore store = new(_sessionsRoot);
        SessionManagerFactory factory = new(store);
        ISessionManager sessionManager = await factory.CreateAsync(Environment.CurrentDirectory, CancellationToken.None);
        return new ChatSession(sessionManager, "local", "coder", renderMarkdown: false);
    }

    private static AgentRunRequest CreateRequest(ChatSession session) =>
        new(
            session.ProviderId,
            session.ModelId,
            session.CreateRunConversation("hi"),
            session.WorkspaceRoot,
            session.ProjectContext,
            session.ReasoningEffort,
            session.ToolFilter,
            session.Steering);

    private static async ValueTask<TurnOutcome> RunPumpAsync(ChatSession session, IAgentRuntime runtime)
    {
        TurnPump pump = new(session);
        return await pump.RunAsync(
            CreateRequest(session),
            runtime,
            static _ => ValueTask.CompletedTask,
            CancellationToken.None);
    }

    private sealed class ScriptedRuntime : IAgentRuntime
    {
        private readonly IReadOnlyList<AgentEvent> _events;

        public ScriptedRuntime(params AgentEvent[] events)
        {
            _events = events;
        }

        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            foreach (AgentEvent agentEvent in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return agentEvent;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class CancellingRuntime : IAgentRuntime
    {
        public async IAsyncEnumerable<AgentEvent> RunAsync(
            AgentRunRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            yield return new AgentEvent(AgentEventKind.AssistantDelta, "partial");
            await Task.CompletedTask;
            throw new OperationCanceledException();
        }
    }
}
