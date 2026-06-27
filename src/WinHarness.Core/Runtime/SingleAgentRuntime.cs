using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;
using WinHarness.Diagnostics;
using WinHarness.Providers;
using WinHarness.Tools;

namespace WinHarness.Runtime;

/// <summary>
/// Initial single-agent runtime implementation.
/// </summary>
public sealed class SingleAgentRuntime : IAgentRuntime
{
    private const string RuntimeSystemPrompt = """
You are WinHarness, a Windows-first coding agent running in a developer workstation environment.

Command execution rules:
- The host shell is Windows/PowerShell, not bash.
- When using run_command, set command to the executable only and put flags in the arguments array.
- Prefer Windows-native commands: where.exe, cmd.exe /c, or pwsh -NoProfile -Command.
- To check whether a CLI exists, use where.exe <name> or pwsh -NoProfile -Command "Get-Command <name>".
- Do not use Unix shell builtins or POSIX-only commands such as command -v, which, bash, sh, ls, cat, rm -rf, chmod, or sudo unless the user explicitly asks for WSL/POSIX and the executable is known to exist.
- Prefer PowerShell equivalents: Get-ChildItem, Get-Content, Remove-Item, New-Item, Set-Content, Join-Path.
""";

    private static readonly IDiagnosticSink NoDiagnostics = new NullDiagnosticSink();

    private readonly IDiagnosticSink _diagnosticSink;
    private readonly IProviderFactory _providerFactory;
    private readonly IEnumerable<IToolProvider> _toolProviders;
    private readonly ILogger<SingleAgentRuntime> _logger;

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(IProviderFactory providerFactory, ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, [], NoDiagnostics, logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, toolProviders, NoDiagnostics, logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        IDiagnosticSink diagnosticSink,
        ILogger<SingleAgentRuntime> logger)
    {
        _providerFactory = providerFactory;
        _toolProviders = toolProviders;
        _diagnosticSink = diagnosticSink;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatMessage> messages = ProjectConversation(request.Conversation);
        IChatProvider provider = _providerFactory.Create(request.ProviderId, request.ModelId);
        using IChatClient client = provider.CreateChatClient();

        _logger.ProviderRequestStarting(provider.ProviderId, provider.ModelId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await WriteProviderDiagnosticAsync(
            "provider.started",
            provider,
            stopwatch,
            cancellationToken).ConfigureAwait(false);

        RuntimeToolActivitySink toolActivitySink = new();
        ChatOptions? options = await CreateChatOptionsAsync(toolActivitySink, cancellationToken).ConfigureAwait(false);
        StringBuilder assistantText = new();

        await using IAsyncEnumerator<ChatResponseUpdate> updates = client.GetStreamingResponseAsync(
                messages,
                options,
                cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        UsageDetails? usage = null;

        while (true)
        {
            ChatResponseUpdate? update = null;
            AgentEvent? failureEvent = null;
            try
            {
                if (!await updates.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                update = updates.Current;
            }
            catch (Exception ex)
            {
                await WriteProviderDiagnosticAsync(
                    "provider.failed",
                    provider,
                    stopwatch,
                    CancellationToken.None,
                    ex).ConfigureAwait(false);

                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                failureEvent = new AgentEvent(AgentEventKind.Failed, ex.Message);
            }

            if (failureEvent is not null)
            {
                yield return failureEvent;
                yield break;
            }

            usage = update!.Contents.OfType<UsageContent>().LastOrDefault()?.Details ?? usage;

            foreach (AgentEvent toolEvent in toolActivitySink.Drain())
            {
                yield return toolEvent;
            }

            string text = update.ToString();
            if (text.Length > 0)
            {
                assistantText.Append(text);
                yield return new AgentEvent(AgentEventKind.AssistantDelta, text);
            }
        }

        foreach (AgentEvent toolEvent in toolActivitySink.Drain())
        {
            yield return toolEvent;
        }

        _logger.ProviderRequestCompleted(provider.ProviderId, provider.ModelId);
        await WriteProviderDiagnosticAsync(
            "provider.completed",
            provider,
            stopwatch,
            cancellationToken,
            usage: usage).ConfigureAwait(false);
        yield return new AgentEvent(
            AgentEventKind.Completed,
            "completed",
            new ConversationMessage(ConversationRole.Assistant, assistantText.ToString()));
    }

    private static IReadOnlyList<ChatMessage> ProjectConversation(ConversationState conversation)
    {
        if (conversation.Messages.Count == 0)
        {
            throw new InvalidOperationException("Conversation must contain at least one user message.");
        }

        ConversationMessage last = conversation.Messages[^1];
        if (last.Role != ConversationRole.User)
        {
            throw new InvalidOperationException("Conversation must end with a user message before running a turn.");
        }

        List<ChatMessage> messages = new(conversation.Messages.Count + 1)
        {
            new(ChatRole.System, RuntimeSystemPrompt)
        };

        foreach (ConversationMessage message in conversation.Messages)
        {
            messages.Add(new ChatMessage(ProjectRole(message.Role), message.Content));
        }

        return messages;
    }

    private static ChatRole ProjectRole(ConversationRole role)
    {
        return role switch
        {
            ConversationRole.System => ChatRole.System,
            ConversationRole.User => ChatRole.User,
            ConversationRole.Assistant => ChatRole.Assistant,
            ConversationRole.Tool => ChatRole.Tool,
            _ => throw new InvalidOperationException($"Unsupported conversation role '{role}'.")
        };
    }

    private async ValueTask<ChatOptions?> CreateChatOptionsAsync(
        IToolActivitySink activitySink,
        CancellationToken cancellationToken)
    {
        List<AITool> tools = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (IToolProvider provider in _toolProviders)
        {
            IReadOnlyList<ITool> providerTools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (ITool tool in providerTools)
            {
                if (!names.Add(tool.Name))
                {
                    throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'.");
                }

                tools.Add(new ToolAIFunctionAdapter(tool, _diagnosticSink, activitySink));
            }
        }

        return tools.Count == 0 ? null : new ChatOptions { Tools = tools };
    }

    private async ValueTask WriteProviderDiagnosticAsync(
        string eventName,
        IChatProvider provider,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        Exception? exception = null,
        UsageDetails? usage = null)
    {
        Dictionary<string, string> properties = new()
        {
            ["provider.id"] = provider.ProviderId,
            ["model.id"] = provider.ModelId,
            ["provider.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["retry.count"] = "0"
        };

        if (exception is not null)
        {
            properties["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name;
        }

        if (usage is not null)
        {
            properties["usage.input_tokens"] = usage.InputTokenCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            properties["usage.output_tokens"] = usage.OutputTokenCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            properties["usage.total_tokens"] = usage.TotalTokenCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            properties["usage.reasoning_tokens"] = usage.ReasoningTokenCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        }

        await _diagnosticSink.WriteAsync(
            new DiagnosticRecord(
                DateTimeOffset.UtcNow,
                "provider",
                eventName,
                $"{provider.ProviderId}/{provider.ModelId}",
                properties),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class RuntimeToolActivitySink : IToolActivitySink
    {
        private readonly ConcurrentQueue<AgentEvent> _events = new();

        public void ToolStarted(string toolName)
        {
            _events.Enqueue(new AgentEvent(AgentEventKind.ToolActivity, $"tool started: {toolName}"));
        }

        public void ToolCompleted(string toolName, ToolResult result, TimeSpan duration)
        {
            string status = result.Succeeded ? "completed" : "failed";
            _events.Enqueue(new AgentEvent(AgentEventKind.ToolActivity, $"tool {status}: {toolName} ({duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms)"));
        }

        public void ToolFailed(string toolName, Exception exception, TimeSpan duration)
        {
            _events.Enqueue(new AgentEvent(AgentEventKind.ToolActivity, $"tool exception: {toolName} ({duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms) {exception.GetType().Name}"));
        }

        public IReadOnlyList<AgentEvent> Drain()
        {
            List<AgentEvent> drained = [];
            while (_events.TryDequeue(out AgentEvent? agentEvent))
            {
                drained.Add(agentEvent);
            }

            return drained;
        }
    }

    private sealed class NullDiagnosticSink : IDiagnosticSink
    {
        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
