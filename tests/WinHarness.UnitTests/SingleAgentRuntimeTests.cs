using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Providers;
using WinHarness.Runtime;
using WinHarness.Tools;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class SingleAgentRuntimeTests
{
    [TestMethod]
    public async Task StreamsAssistantDeltasInOrder()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["hello ", "WinHarness"]),
            NullLogger<SingleAgentRuntime>.Instance);

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.AreEqual(3, events.Count);
        Assert.AreEqual(AgentEventKind.AssistantDelta, events[0].Kind);
        Assert.AreEqual("hello ", events[0].Message);
        Assert.AreEqual("WinHarness", events[1].Message);
        Assert.AreEqual(AgentEventKind.Completed, events[2].Kind);
        Assert.IsNotNull(events[2].AssistantMessage);
        Assert.AreEqual(ConversationRole.Assistant, events[2].AssistantMessage!.Role);
        Assert.AreEqual("hello WinHarness", events[2].AssistantMessage!.Content);
    }

    [TestMethod]
    public async Task HonorsCancellationDuringStreaming()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["one", "two"]),
            NullLogger<SingleAgentRuntime>.Instance);
        using CancellationTokenSource cancellation = new();

        IAsyncEnumerator<AgentEvent> enumerator = runtime.RunAsync(
            CreateRequest("prompt"),
            cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Assert.IsTrue(await enumerator.MoveNextAsync());
        await cancellation.CancelAsync();

        OperationCanceledException? exception = null;
        try
        {
            _ = await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException caught)
        {
            exception = caught;
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.IsNotNull(exception);
    }

    [TestMethod]
    public async Task PassesToolsToChatClient()
    {
        RecordingDiagnosticSink diagnostics = new();
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory([]),
            [new FakeToolProvider()],
            diagnostics,
            NullLogger<SingleAgentRuntime>.Instance);

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.IsTrue(events.Any(static agentEvent =>
            agentEvent.Kind == AgentEventKind.ToolActivity &&
            agentEvent.Message.Contains("tool started", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(static agentEvent =>
            agentEvent.Kind == AgentEventKind.ToolActivity &&
            agentEvent.Message.Contains("tool completed", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(static agentEvent =>
            agentEvent.Kind == AgentEventKind.AssistantDelta &&
            agentEvent.Message == "tool says pong"));
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "provider.completed"));
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "tool.completed"));
        DiagnosticRecord toolRecord = diagnostics.Records.Single(static record => record.EventName == "tool.completed");
        Assert.AreEqual("value", toolRecord.Properties["custom.metadata"]);
    }

    [TestMethod]
    public async Task RecordsUsageDiagnosticsWhenProviderReportsTokens()
    {
        RecordingDiagnosticSink diagnostics = new();
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["hello"]),
            [],
            diagnostics,
            NullLogger<SingleAgentRuntime>.Instance);

        await foreach (AgentEvent _ in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
        }

        DiagnosticRecord completed = diagnostics.Records.Single(static record => record.EventName == "provider.completed");
        Assert.AreEqual("3", completed.Properties["usage.input_tokens"]);
        Assert.AreEqual("5", completed.Properties["usage.output_tokens"]);
        Assert.AreEqual("8", completed.Properties["usage.total_tokens"]);
        Assert.AreEqual("0", completed.Properties["retry.count"]);
    }

    [TestMethod]
    public async Task EmitsFailedEventWhenProviderFails()
    {
        RecordingDiagnosticSink diagnostics = new();
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["boom"], failOnStream: true),
            [],
            diagnostics,
            NullLogger<SingleAgentRuntime>.Instance);

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(AgentEventKind.Failed, events[0].Kind);
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "provider.failed"));
    }


    [TestMethod]
    public async Task SendsConversationMessagesToChatClient()
    {
        Conversation.Conversation conversation = new();
        conversation.Add(new ConversationMessage(ConversationRole.System, "system instructions"));
        conversation.Add(new ConversationMessage(ConversationRole.User, "remember my name"));
        conversation.Add(new ConversationMessage(ConversationRole.Assistant, "ok"));
        conversation.Add(new ConversationMessage(ConversationRole.User, "what is my name?"));

        FakeProviderFactory providerFactory = new(["Dave"]);
        SingleAgentRuntime runtime = new(providerFactory, NullLogger<SingleAgentRuntime>.Instance);

        await foreach (AgentEvent _ in runtime.RunAsync(
                           new AgentRunRequest("test-provider", "test-model", conversation),
                           CancellationToken.None))
        {
        }

        CollectionAssert.AreEqual(
            new[] { ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User },
            providerFactory.LastMessages.Select(static message => message.Role).ToArray());
        CollectionAssert.AreEqual(
            new[] { "system instructions", "remember my name", "ok", "what is my name?" },
            providerFactory.LastMessages.Select(static message => message.Text).ToArray());
    }

    [TestMethod]
    public async Task RejectsConversationThatDoesNotEndWithUserMessage()
    {
        Conversation.Conversation conversation = new();
        conversation.Add(new ConversationMessage(ConversationRole.Assistant, "not a user turn"));
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["ignored"]),
            NullLogger<SingleAgentRuntime>.Instance);

        InvalidOperationException? exception = null;
        try
        {
            await foreach (AgentEvent _ in runtime.RunAsync(
                               new AgentRunRequest("test-provider", "test-model", conversation),
                               CancellationToken.None))
            {
            }
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception.Message, "end with a user message");
    }

    private static AgentRunRequest CreateRequest(string prompt)
    {
        Conversation.Conversation conversation = new();
        conversation.Add(new ConversationMessage(ConversationRole.User, prompt));
        return new AgentRunRequest("test-provider", "test-model", conversation);
    }

    private sealed class FakeProviderFactory : IProviderFactory
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;

        public FakeProviderFactory(IReadOnlyList<string> updates, bool failOnStream = false)
        {
            _updates = updates;
            _failOnStream = failOnStream;
        }

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public IChatProvider Create(string providerId, string modelId)
        {
            return new FakeProvider(providerId, modelId, _updates, _failOnStream, messages => LastMessages = messages);
        }
    }

    private sealed class FakeProvider : IChatProvider
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;
        private readonly Action<IReadOnlyList<ChatMessage>> _captureMessages;

        public FakeProvider(
            string providerId,
            string modelId,
            IReadOnlyList<string> updates,
            bool failOnStream,
            Action<IReadOnlyList<ChatMessage>> captureMessages)
        {
            ProviderId = providerId;
            ModelId = modelId;
            _updates = updates;
            _failOnStream = failOnStream;
            _captureMessages = captureMessages;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IChatClient CreateChatClient()
        {
            return new FakeChatClient(_updates, _failOnStream, _captureMessages);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;
        private readonly Action<IReadOnlyList<ChatMessage>> _captureMessages;

        public FakeChatClient(
            IReadOnlyList<string> updates,
            bool failOnStream,
            Action<IReadOnlyList<ChatMessage>> captureMessages)
        {
            _updates = updates;
            _failOnStream = failOnStream;
            _captureMessages = captureMessages;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _captureMessages(messages.ToList());

            if (_failOnStream)
            {
                await Task.Yield();
                throw new InvalidOperationException("provider exploded");
            }

            AIFunction? function = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
            if (function is not null)
            {
                object? result = await function.InvokeAsync(
                    new AIFunctionArguments { ["message"] = "ping" },
                    cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "tool says " + result);
                yield break;
            }

            foreach (string update in _updates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, update);
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new UsageContent(new UsageDetails
                {
                    InputTokenCount = 3,
                    OutputTokenCount = 5,
                    TotalTokenCount = 8
                })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeToolProvider : IToolProvider
    {
        private static readonly IReadOnlyList<ITool> Tools = [new FakeTool()];

        public ValueTask<IReadOnlyList<ITool>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Tools);
        }
    }

    private sealed class FakeTool : ITool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"message":{"type":"string"}}}""").RootElement.Clone();

        public string Name => "fake_tool";

        public string Description => "Fake runtime tool.";

        public JsonElement InputSchema => Schema;

        public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Assert.AreEqual("ping", invocation.Arguments.GetProperty("message").GetString());
            return ValueTask.FromResult(new ToolResult(
                true,
                "pong",
                Metadata: new Dictionary<string, string> { ["custom.metadata"] = "value" }));
        }
    }

    private sealed class RecordingDiagnosticSink : IDiagnosticSink
    {
        public List<DiagnosticRecord> Records { get; } = [];

        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
