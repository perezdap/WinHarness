using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;
using WinHarness.Runtime;

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

    private sealed class FakeProviderFactory : IProviderFactory
    {
        private readonly IReadOnlyList<string> _updates;

        public FakeProviderFactory(IReadOnlyList<string> updates)
        {
            _updates = updates;
        }

        public IChatProvider Create(string providerId, string modelId)
        {
            return new FakeProvider(providerId, modelId, _updates);
        }
    }

    private sealed class FakeProvider : IChatProvider
    {
        private readonly IReadOnlyList<string> _updates;

        public FakeProvider(string providerId, string modelId, IReadOnlyList<string> updates)
        {
            ProviderId = providerId;
            ModelId = modelId;
            _updates = updates;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IChatClient CreateChatClient()
        {
            return new FakeChatClient(_updates);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _updates;

        public FakeChatClient(IReadOnlyList<string> updates)
        {
            _updates = updates;
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
            foreach (string update in _updates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, update);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
