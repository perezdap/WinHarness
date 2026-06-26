using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
                           new AgentRunRequest("test-provider", "test-model", "prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.AreEqual(3, events.Count);
        Assert.AreEqual(AgentEventKind.AssistantDelta, events[0].Kind);
        Assert.AreEqual("hello ", events[0].Message);
        Assert.AreEqual("WinHarness", events[1].Message);
        Assert.AreEqual(AgentEventKind.Completed, events[2].Kind);
    }

    [TestMethod]
    public async Task HonorsCancellationDuringStreaming()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["one", "two"]),
            NullLogger<SingleAgentRuntime>.Instance);
        using CancellationTokenSource cancellation = new();

        IAsyncEnumerator<AgentEvent> enumerator = runtime.RunAsync(
            new AgentRunRequest("test-provider", "test-model", "prompt"),
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
                           new AgentRunRequest("test-provider", "test-model", "prompt"),
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
                           new AgentRunRequest("test-provider", "test-model", "prompt"),
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
                           new AgentRunRequest("test-provider", "test-model", "prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(AgentEventKind.Failed, events[0].Kind);
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "provider.failed"));
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

        public IChatProvider Create(string providerId, string modelId)
        {
            return new FakeProvider(providerId, modelId, _updates, _failOnStream);
        }
    }

    private sealed class FakeProvider : IChatProvider
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;

        public FakeProvider(string providerId, string modelId, IReadOnlyList<string> updates, bool failOnStream)
        {
            ProviderId = providerId;
            ModelId = modelId;
            _updates = updates;
            _failOnStream = failOnStream;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IChatClient CreateChatClient()
        {
            return new FakeChatClient(_updates, _failOnStream);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;

        public FakeChatClient(IReadOnlyList<string> updates, bool failOnStream)
        {
            _updates = updates;
            _failOnStream = failOnStream;
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
