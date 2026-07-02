using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Infrastructure.Context;
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
        TurnArtifacts artifacts = events[2].TurnArtifacts!;
        Assert.IsNotNull(artifacts);
        Assert.AreEqual(2, artifacts.Messages.Count);
        Assert.AreEqual(ConversationRole.User, artifacts.Messages[0].Role);
        Assert.AreEqual("prompt", artifacts.Messages[0].Text);
        Assert.AreEqual(ConversationRole.Assistant, artifacts.Messages[1].Role);
        Assert.AreEqual("hello WinHarness", artifacts.Messages[1].Text);
        Assert.AreEqual("test-provider", artifacts.Messages[1].ProviderId);
        Assert.AreEqual("test-model", artifacts.Messages[1].ModelId);
    }

    [TestMethod]
    public async Task SavesPartialTurnArtifactsWhenProviderFailsAfterStreaming()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["partial "], failAfterUpdates: 0),
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
        Assert.AreEqual("partial ", events[0].Message);
        Assert.AreEqual(AgentEventKind.Failed, events[1].Kind);
        Assert.AreEqual(AgentEventKind.Completed, events[2].Kind);
        Assert.AreEqual("partial", events[2].Message);
        TurnArtifacts artifacts = events[2].TurnArtifacts!;
        Assert.AreEqual(2, artifacts.Messages.Count);
        Assert.AreEqual("partial ", artifacts.Messages[1].Text);
    }

    [TestMethod]
    public async Task StopsRunawayAssistantStreamAndSavesPartialArtifacts()
    {
        string runawayText = new('x', (256 * 1024) + 1);
        RecordingDiagnosticSink diagnostics = new();
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory([runawayText]),
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

        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(AgentEventKind.Failed, events[0].Kind);
        StringAssert.Contains(events[0].Message, "per-turn text limit");
        Assert.AreEqual(AgentEventKind.Completed, events[1].Kind);
        Assert.AreEqual("partial", events[1].Message);
        Assert.AreEqual(256 * 1024, events[1].TurnArtifacts!.Messages[1].Text.Length);
        Assert.AreEqual(runawayText[..(256 * 1024)], events[1].TurnArtifacts!.Messages[1].Text);
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "provider.failed"));
        Assert.IsFalse(diagnostics.Records.Any(static record => record.EventName == "provider.completed"));
    }

    [TestMethod]
    public async Task StopsLowNoveltyRepetitionLoopUnderSizeCap()
    {
        // Mirrors the observed failure: the model restates the same intent with slight
        // rewording and re-emits empty PowerShell code boxes of varying width. Stays far
        // under the 256 KB byte cap, so only the novelty detector can catch it.
        List<string> updates = [];
        for (int cycle = 0; cycle < 12; cycle++)
        {
            int width = 60 + cycle; // vary box width so borders are not byte-identical
            updates.Add($"Let me set session.ReasoningEffort in RunTurnAsync (attempt {cycle % 2}):\n");
            updates.Add("╭─code" + new string('─', width) + "╮\n");
            updates.Add("│ " + new string(' ', width) + " │\n");
            updates.Add("│ Let me set the effort after creating the session: ```pwsh" + new string(' ', cycle) + " │\n");
            updates.Add("╰" + new string('─', width + 4) + "╯\n");
            updates.Add("\n");
        }

        RecordingDiagnosticSink diagnostics = new();
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(updates),
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

        AgentEvent failed = events.Single(static agentEvent => agentEvent.Kind == AgentEventKind.Failed);
        StringAssert.Contains(failed.Message, "repeating low-novelty output");
        AgentEvent completed = events.Single(static agentEvent => agentEvent.Kind == AgentEventKind.Completed);
        Assert.AreEqual("partial", completed.Message);
        Assert.IsTrue(completed.TurnArtifacts!.Messages[1].Text.Length < 256 * 1024);
        Assert.IsTrue(diagnostics.Records.Any(static record => record.EventName == "provider.failed"));
        Assert.IsFalse(diagnostics.Records.Any(static record => record.EventName == "provider.completed"));
    }

    [TestMethod]
    public async Task DoesNotFlagNormalVariedOutputAsRepetitionLoop()
    {
        // A long but genuinely varied response must stream to completion untouched.
        List<string> updates = [];
        for (int line = 0; line < 80; line++)
        {
            updates.Add($"Step {line}: do a distinct thing involving widget {line} and value {line * 7}.\n");
        }

        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(updates),
            NullLogger<SingleAgentRuntime>.Instance);

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        Assert.IsFalse(events.Any(static agentEvent => agentEvent.Kind == AgentEventKind.Failed));
        AgentEvent completed = events.Single(static agentEvent => agentEvent.Kind == AgentEventKind.Completed);
        Assert.AreEqual("completed", completed.Message);
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
    public async Task ToolFilterExcludesToolsFromChatOptions()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["plain answer"]),
            [new FakeToolProvider()],
            new RecordingDiagnosticSink(),
            NullLogger<SingleAgentRuntime>.Instance);

        Conversation.Conversation conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "prompt"));
        AgentRunRequest request = new(
            "test-provider",
            "test-model",
            conversation,
            ToolFilter: new ToolFilter(Exclude: ["fake_tool"]));

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(request, CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        // With the only tool excluded, the fake provider sees no functions and
        // streams plain text; no tool activity may occur.
        Assert.IsFalse(events.Any(static agentEvent => agentEvent.Kind == AgentEventKind.ToolActivity));
        Assert.IsTrue(events.Any(static agentEvent =>
            agentEvent.Kind == AgentEventKind.AssistantDelta &&
            agentEvent.Message == "plain answer"));
    }

    [TestMethod]
    public async Task SteeringMessagesInjectedBetweenToolRoundTrips()
    {
        List<IReadOnlyList<ChatMessage>> captured = [];
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory([], captureMessages: captured.Add),
            [new FakeToolProvider()],
            new RecordingDiagnosticSink(),
            NullLogger<SingleAgentRuntime>.Instance);

        Conversation.Conversation conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "prompt"));
        SteeringQueue steering = new();
        steering.Enqueue("also check the tests");
        AgentRunRequest request = new(
            "test-provider",
            "test-model",
            conversation,
            Steering: steering);

        List<AgentEvent> events = [];
        await foreach (AgentEvent agentEvent in runtime.RunAsync(request, CancellationToken.None))
        {
            events.Add(agentEvent);
        }

        // The second inner call (after the tool result) must carry the steering
        // message as a user message after the tool result.
        Assert.AreEqual(2, captured.Count);
        Assert.IsFalse(captured[0].Any(static message =>
            message.Role == ChatRole.User && message.Text == "also check the tests"));
        Assert.IsTrue(captured[1].Any(static message =>
            message.Role == ChatRole.User && message.Text == "also check the tests"));
        Assert.AreEqual(0, steering.Count);

        // Steering must be persisted in the turn artifacts.
        AgentEvent completed = events.Single(static agentEvent => agentEvent.Kind == AgentEventKind.Completed);
        Assert.IsTrue(completed.TurnArtifacts!.Messages.Any(static message =>
            message.Role == ConversationRole.User && message.Text == "also check the tests"));
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

        AgentEvent completed = events.Single(static agentEvent => agentEvent.Kind == AgentEventKind.Completed);
        TurnArtifacts artifacts = completed.TurnArtifacts!;
        Assert.IsTrue(artifacts.Messages.Count >= 4);
        Assert.AreEqual(ConversationRole.User, artifacts.Messages[0].Role);
        Assert.AreEqual("prompt", artifacts.Messages[0].Text);

        ConversationMessage assistantWithToolCall = artifacts.Messages[1];
        Assert.AreEqual(ConversationRole.Assistant, assistantWithToolCall.Role);
        ContentBlock toolCall = assistantWithToolCall.Content.Single(static block => block.Kind == ContentBlockKind.ToolCall);
        Assert.AreEqual("fake_tool", toolCall.ToolName);
        Assert.IsTrue(toolCall.ArgumentsJson!.Contains("\"message\":\"ping\"", StringComparison.Ordinal));

        ConversationMessage toolResult = artifacts.Messages[2];
        Assert.AreEqual(ConversationRole.Tool, toolResult.Role);
        ContentBlock resultBlock = toolResult.Content.Single();
        Assert.AreEqual(ContentBlockKind.ToolResult, resultBlock.Kind);
        Assert.AreEqual("fake_tool", resultBlock.ToolName);
        Assert.AreEqual("pong", resultBlock.Text);
        Assert.AreEqual(toolCall.ToolCallId, resultBlock.ToolCallId);

        ConversationMessage finalAssistant = artifacts.Messages[^1];
        Assert.AreEqual(ConversationRole.Assistant, finalAssistant.Role);
        Assert.AreEqual("tool says pong", finalAssistant.Text);
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
    public async Task CompletedEventIncludesUsageOnAssistantMessage()
    {
        SingleAgentRuntime runtime = new(
            new FakeProviderFactory(["hello"]),
            NullLogger<SingleAgentRuntime>.Instance);

        AgentEvent? completed = null;
        await foreach (AgentEvent agentEvent in runtime.RunAsync(
                           CreateRequest("prompt"),
                           CancellationToken.None))
        {
            if (agentEvent.Kind == AgentEventKind.Completed)
            {
                completed = agentEvent;
            }
        }

        Assert.IsNotNull(completed?.TurnArtifacts);
        ConversationMessage assistant = completed.TurnArtifacts!.Messages[^1];
        Assert.IsNotNull(assistant.Usage);
        Assert.AreEqual(3, assistant.Usage!.InputTokens);
        Assert.AreEqual(5, assistant.Usage.OutputTokens);
        Assert.AreEqual(8, assistant.Usage.TotalTokens);
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
        conversation.Add(ConversationMessage.FromText(ConversationRole.System, "system instructions"));
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "remember my name"));
        conversation.Add(ConversationMessage.FromText(ConversationRole.Assistant, "ok"));
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "what is my name?"));

        FakeProviderFactory providerFactory = new(["Dave"]);
        SingleAgentRuntime runtime = new(providerFactory, NullLogger<SingleAgentRuntime>.Instance);

        await foreach (AgentEvent _ in runtime.RunAsync(
                           new AgentRunRequest("test-provider", "test-model", conversation),
                           CancellationToken.None))
        {
        }

        CollectionAssert.AreEqual(
            new[] { ChatRole.System, ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User },
            providerFactory.LastMessages.Select(static message => message.Role).ToArray());
        StringAssert.Contains(providerFactory.LastMessages[0].Text, "Windows/PowerShell");
        CollectionAssert.AreEqual(
            new[] { "system instructions", "remember my name", "ok", "what is my name?" },
            providerFactory.LastMessages.Skip(1).Select(static message => message.Text).ToArray());
    }

    [TestMethod]
    public async Task InjectsWindowsSystemPromptWhenConversationHasNone()
    {
        FakeProviderFactory providerFactory = new(["ok"]);
        SingleAgentRuntime runtime = new(providerFactory, NullLogger<SingleAgentRuntime>.Instance);

        await foreach (AgentEvent _ in runtime.RunAsync(CreateRequest("check firecrawl"), CancellationToken.None))
        {
        }

        Assert.AreEqual(ChatRole.System, providerFactory.LastMessages[0].Role);
        StringAssert.Contains(providerFactory.LastMessages[0].Text, "Windows/PowerShell");
        StringAssert.Contains(providerFactory.LastMessages[0].Text, "where.exe");
        CollectionAssert.AreEqual(
            new[] { ChatRole.System, ChatRole.User },
            providerFactory.LastMessages.Select(static message => message.Role).ToArray());
    }

    [TestMethod]
    public async Task PrependsBaseSystemPromptEvenWhenConversationHasSystemMessage()
    {
        Conversation.Conversation conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.System, "skill instructions"));
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, "go"));
        FakeProviderFactory providerFactory = new(["ok"]);
        SingleAgentRuntime runtime = new(providerFactory, NullLogger<SingleAgentRuntime>.Instance);

        await foreach (AgentEvent _ in runtime.RunAsync(
                           new AgentRunRequest("test-provider", "test-model", conversation),
                           CancellationToken.None))
        {
        }

        Assert.AreEqual(ChatRole.System, providerFactory.LastMessages[0].Role);
        StringAssert.Contains(providerFactory.LastMessages[0].Text, "Windows/PowerShell");
        Assert.AreEqual("skill instructions", providerFactory.LastMessages[1].Text);
    }

    [TestMethod]
    public async Task InjectsAgentsInstructionsFromContextLoader()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "WinHarnessRuntimeContext", Guid.NewGuid().ToString("N"));
        string globalConfigDirectory = Path.Combine(tempRoot, "global-config");
        string workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(globalConfigDirectory);
        Directory.CreateDirectory(workspaceRoot);
        File.WriteAllText(Path.Combine(workspaceRoot, "AGENTS.md"), "Use the purple widget pattern.");

        try
        {
            ContextFileLoader loader = new(globalConfigDirectory);
            FakeProviderFactory providerFactory = new(["ok"]);
            SingleAgentRuntime runtime = new(
                providerFactory,
                [],
                new RecordingDiagnosticSink(),
                loader,
                NullLogger<SingleAgentRuntime>.Instance);

            await foreach (AgentEvent _ in runtime.RunAsync(
                               CreateRequest("go", workspaceRoot),
                               CancellationToken.None))
            {
            }

            Assert.IsTrue(providerFactory.LastMessages.Count >= 2);
            Assert.AreEqual(ChatRole.System, providerFactory.LastMessages[0].Role);
            StringAssert.Contains(providerFactory.LastMessages[0].Text, "Windows/PowerShell");
            Assert.AreEqual(ChatRole.System, providerFactory.LastMessages[1].Role);
            StringAssert.Contains(providerFactory.LastMessages[1].Text, "purple widget pattern");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RejectsConversationThatDoesNotEndWithUserMessage()
    {
        Conversation.Conversation conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.Assistant, "not a user turn"));
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

    private static AgentRunRequest CreateRequest(string prompt, string? workspaceRoot = null)
    {
        Conversation.Conversation conversation = new();
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, prompt));
        return new AgentRunRequest(
            "test-provider",
            "test-model",
            conversation,
            workspaceRoot ?? string.Empty);
    }

    private sealed class FakeProviderFactory : IProviderFactory
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;
        private readonly int _failAfterUpdates;
        private readonly Action<IReadOnlyList<ChatMessage>>? _captureEachCall;

        public FakeProviderFactory(
            IReadOnlyList<string> updates,
            bool failOnStream = false,
            int failAfterUpdates = -1,
            Action<IReadOnlyList<ChatMessage>>? captureMessages = null)
        {
            _updates = updates;
            _failOnStream = failOnStream;
            _failAfterUpdates = failAfterUpdates;
            _captureEachCall = captureMessages;
        }

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public IChatProvider Create(string providerId, string modelId)
        {
            return new FakeProvider(
                providerId,
                modelId,
                _updates,
                _failOnStream,
                _failAfterUpdates,
                messages =>
                {
                    LastMessages = messages;
                    _captureEachCall?.Invoke(messages);
                });
        }
    }

    private sealed class FakeProvider : IChatProvider
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;
        private readonly int _failAfterUpdates;
        private readonly Action<IReadOnlyList<ChatMessage>> _captureMessages;

        public FakeProvider(
            string providerId,
            string modelId,
            IReadOnlyList<string> updates,
            bool failOnStream,
            int failAfterUpdates,
            Action<IReadOnlyList<ChatMessage>> captureMessages)
        {
            ProviderId = providerId;
            ModelId = modelId;
            _updates = updates;
            _failOnStream = failOnStream;
            _failAfterUpdates = failAfterUpdates;
            _captureMessages = captureMessages;
        }

        public string ProviderId { get; }

        public string ModelId { get; }

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IChatClient CreateChatClient()
        {
            return new FakeChatClient(_updates, _failOnStream, _failAfterUpdates, _captureMessages);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly IReadOnlyList<string> _updates;
        private readonly bool _failOnStream;
        private readonly int _failAfterUpdates;
        private readonly Action<IReadOnlyList<ChatMessage>> _captureMessages;

        public FakeChatClient(
            IReadOnlyList<string> updates,
            bool failOnStream,
            int failAfterUpdates,
            Action<IReadOnlyList<ChatMessage>> captureMessages)
        {
            _updates = updates;
            _failOnStream = failOnStream;
            _failAfterUpdates = failAfterUpdates;
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

            bool hasToolResult = messages.Any(static message =>
                message.Role == ChatRole.Tool &&
                message.Contents.Any(static content => content is FunctionResultContent));
            AIFunction? function = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
            if (function is not null && !hasToolResult)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call_01", function.Name, new Dictionary<string, object?> { ["message"] = "ping" })]);
                yield break;
            }

            if (function is not null && hasToolResult)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "tool says pong");
                yield break;
            }

            int yieldedUpdates = 0;
            foreach (string update in _updates)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, update);
                yieldedUpdates++;
                if (_failAfterUpdates >= 0 && yieldedUpdates > _failAfterUpdates)
                {
                    throw new InvalidOperationException("provider exploded");
                }
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
