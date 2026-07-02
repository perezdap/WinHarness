using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Context;
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

    // Safety net for a single streamed response that never stops (e.g. a model that
    // loops the same empty code fence forever). The function-invocation loop is already
    // bounded by Microsoft.Extensions.AI, but a runaway *within one response* is not, so
    // we cap accumulated text and raw update count per turn and fail cleanly if exceeded.
    private const int MaxAssistantCharactersPerTurn = 256 * 1024;
    private const int MaxStreamingUpdatesPerTurn = 100_000;

    // Catches degenerate repetition loops that stay under the size cap: a model that
    // restates the same paragraph (slightly reworded) and re-emits empty code fences.
    // Once a sliding window of recent normalized lines is full, an extremely low count
    // of distinct lines means the response is stuck repeating itself.
    private const int OutputLoopWindowLines = 32;
    private const int OutputLoopMaxDistinctLines = 12;

    private static readonly IDiagnosticSink NoDiagnostics = new NullDiagnosticSink();

    private readonly IContextFileLoader? _contextFileLoader;
    private readonly IDiagnosticSink _diagnosticSink;
    private readonly IProviderFactory _providerFactory;
    private readonly IEnumerable<IToolProvider> _toolProviders;
    private readonly ILogger<SingleAgentRuntime> _logger;

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(IProviderFactory providerFactory, ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, [], NoDiagnostics, null, logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, toolProviders, NoDiagnostics, null, logger)
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
        : this(providerFactory, toolProviders, diagnosticSink, null, logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        IDiagnosticSink diagnosticSink,
        IContextFileLoader? contextFileLoader,
        ILogger<SingleAgentRuntime> logger)
    {
        _providerFactory = providerFactory;
        _toolProviders = toolProviders;
        _diagnosticSink = diagnosticSink;
        _contextFileLoader = contextFileLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ProjectContext projectContext = ResolveProjectContext(request);
        IReadOnlyList<ChatMessage> messages = ProjectConversation(request.Conversation, projectContext);
        IChatProvider provider = _providerFactory.Create(request.ProviderId, request.ModelId);
        (TurnRecorderChatClient recorder, IChatClient chatClient) = TurnRecorderChatClient.Create(provider.CreateChatClient());
        using IChatClient client = chatClient;

        _logger.ProviderRequestStarting(provider.ProviderId, provider.ModelId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await WriteProviderDiagnosticAsync(
            "provider.started",
            provider,
            stopwatch,
            cancellationToken).ConfigureAwait(false);

        RuntimeToolActivitySink toolActivitySink = new();
        ChatOptions? options = await CreateChatOptionsAsync(toolActivitySink, request.ReasoningEffort, cancellationToken).ConfigureAwait(false);
        StringBuilder assistantText = new();
        OutputLoopDetector loopDetector = new(OutputLoopWindowLines, OutputLoopMaxDistinctLines);

        await using IAsyncEnumerator<ChatResponseUpdate> updates = client.GetStreamingResponseAsync(
                messages,
                options,
                cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        UsageDetails? usage = null;
        int updateCount = 0;

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
                updateCount++;
                if (updateCount > MaxStreamingUpdatesPerTurn)
                {
                    string message = $"Response exceeded the per-turn streaming limit of {MaxStreamingUpdatesPerTurn} updates and was stopped to prevent a runaway loop.";
                    await WriteProviderDiagnosticAsync(
                        "provider.failed",
                        provider,
                        stopwatch,
                        CancellationToken.None,
                        new InvalidOperationException(message)).ConfigureAwait(false);
                    failureEvent = new AgentEvent(AgentEventKind.Failed, message);
                }
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

                if (assistantText.Length > 0)
                {
                    ConversationMessage failedUserMessage = request.Conversation.Messages[^1];
                    yield return new AgentEvent(
                        AgentEventKind.Completed,
                        "partial",
                        new TurnArtifacts(
                        [
                            failedUserMessage,
                            ConversationMessage.FromText(ConversationRole.Assistant, assistantText.ToString())
                        ]));
                }

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
                int remainingCharacterBudget = MaxAssistantCharactersPerTurn - assistantText.Length;
                if (text.Length > remainingCharacterBudget)
                {
                    if (remainingCharacterBudget > 0)
                    {
                        assistantText.Append(text.AsSpan(0, remainingCharacterBudget));
                    }

                    string message = $"Response exceeded the per-turn text limit of {MaxAssistantCharactersPerTurn} characters and was stopped to prevent a runaway loop.";
                    await WriteProviderDiagnosticAsync(
                        "provider.failed",
                        provider,
                        stopwatch,
                        CancellationToken.None,
                        new InvalidOperationException(message)).ConfigureAwait(false);
                    yield return new AgentEvent(AgentEventKind.Failed, message);

                    ConversationMessage failedUserMessage = request.Conversation.Messages[^1];
                    yield return new AgentEvent(
                        AgentEventKind.Completed,
                        "partial",
                        new TurnArtifacts(
                        [
                            failedUserMessage,
                            ConversationMessage.FromText(ConversationRole.Assistant, assistantText.ToString())
                        ]));
                    yield break;
                }

                assistantText.Append(text);
                if (loopDetector.Observe(text))
                {
                    string message = "Response appears to be repeating low-novelty output and was stopped to prevent a runaway loop.";
                    await WriteProviderDiagnosticAsync(
                        "provider.failed",
                        provider,
                        stopwatch,
                        CancellationToken.None,
                        new InvalidOperationException(message)).ConfigureAwait(false);
                    yield return new AgentEvent(AgentEventKind.Failed, message);

                    ConversationMessage failedUserMessage = request.Conversation.Messages[^1];
                    yield return new AgentEvent(
                        AgentEventKind.Completed,
                        "partial",
                        new TurnArtifacts(
                        [
                            failedUserMessage,
                            ConversationMessage.FromText(ConversationRole.Assistant, assistantText.ToString())
                        ]));
                    yield break;
                }

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
        ConversationMessage userMessage = request.Conversation.Messages[^1];
        MessageUsage? messageUsage = usage is null
            ? null
            : new MessageUsage(
                usage.InputTokenCount,
                usage.OutputTokenCount,
                usage.TotalTokenCount);

        yield return new AgentEvent(
            AgentEventKind.Completed,
            "completed",
            recorder.BuildTurnArtifacts(
                userMessage,
                provider.ProviderId,
                provider.ModelId,
                messageUsage));
    }

    private ProjectContext ResolveProjectContext(AgentRunRequest request)
    {
        if (request.ProjectContext is not null)
        {
            return request.ProjectContext;
        }

        if (_contextFileLoader is null)
        {
            return new ProjectContext(null, null, string.Empty);
        }

        string workspaceRoot = string.IsNullOrWhiteSpace(request.WorkspaceRoot)
            ? Environment.CurrentDirectory
            : request.WorkspaceRoot;

        return _contextFileLoader.Load(workspaceRoot);
    }

    private static IReadOnlyList<ChatMessage> ProjectConversation(
        ConversationState conversation,
        ProjectContext projectContext)
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

        List<ChatMessage> messages = new(conversation.Messages.Count + 4);
        string baseSystemPrompt = string.IsNullOrWhiteSpace(projectContext.SystemPromptReplacement)
            ? RuntimeSystemPrompt
            : projectContext.SystemPromptReplacement;
        messages.Add(new(ChatRole.System, baseSystemPrompt));

        if (!string.IsNullOrWhiteSpace(projectContext.SystemPromptAppend))
        {
            messages.Add(new(ChatRole.System, projectContext.SystemPromptAppend));
        }

        if (!string.IsNullOrWhiteSpace(projectContext.AgentsInstructions))
        {
            messages.Add(new(ChatRole.System, projectContext.AgentsInstructions));
        }

        foreach (ConversationMessage message in conversation.Messages)
        {
            messages.AddRange(ProjectMessage(message));
        }

        return messages;
    }

    private static IEnumerable<ChatMessage> ProjectMessage(ConversationMessage message)
    {
        ChatRole role = ProjectRole(message.Role);

        if (message.Role == ConversationRole.Tool)
        {
            foreach (ContentBlock block in message.Content)
            {
                if (block.Kind != ContentBlockKind.ToolResult ||
                    block.ToolCallId is null ||
                    block.ToolName is null ||
                    block.Text is null)
                {
                    continue;
                }

                yield return new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent(block.ToolCallId, block.Text)]);
            }

            yield break;
        }

        List<AIContent> contents = [];
        foreach (ContentBlock block in message.Content)
        {
            switch (block.Kind)
            {
                case ContentBlockKind.Text when block.Text is not null:
                    contents.Add(new TextContent(block.Text));
                    break;
                case ContentBlockKind.ToolCall
                    when block.ToolCallId is not null &&
                         block.ToolName is not null &&
                         block.ArgumentsJson is not null:
                    contents.Add(new FunctionCallContent(
                        block.ToolCallId,
                        block.ToolName,
                        ParseToolArguments(block.ArgumentsJson)));
                    break;
            }
        }

        if (contents.Count == 0)
        {
            yield return new ChatMessage(role, string.Empty);
            yield break;
        }

        if (contents.Count == 1 && contents[0] is TextContent textContent)
        {
            yield return new ChatMessage(role, textContent.Text);
            yield break;
        }

        yield return new ChatMessage(role, contents);
    }

    private static Dictionary<string, object?> ParseToolArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        Dictionary<string, object?> arguments = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = ConvertJsonElement(property.Value);
        }

        return arguments;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long value) => value,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.GetRawText(),
            JsonValueKind.Array => element.GetRawText(),
            _ => element.GetRawText()
        };
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
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        List<AITool> tools = [];
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (IToolProvider provider in _toolProviders)
        {
            IReadOnlyList<ITool> providerTools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (ITool tool in providerTools)
            {
                ToolAIFunctionAdapter adapter = new(tool, _diagnosticSink, activitySink);
                if (!names.Add(adapter.Name))
                {
                    throw new InvalidOperationException($"Duplicate model-facing tool name '{adapter.Name}'.");
                }

                tools.Add(adapter);
            }
        }

        ReasoningOptions? reasoning = ParseReasoningEffort(reasoningEffort);
        if (tools.Count == 0 && reasoning is null)
        {
            return null;
        }

        ChatOptions options = new();
        if (tools.Count > 0)
        {
            options.Tools = tools;
        }

        if (reasoning is not null)
        {
            options.Reasoning = reasoning;
        }

        return options;
    }

    private static ReasoningOptions? ParseReasoningEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
        {
            return null;
        }

        ReasoningEffort parsed = effort.Trim().ToLowerInvariant() switch
        {
            "none" => ReasoningEffort.None,
            "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "extra-high" or "extrahigh" => ReasoningEffort.ExtraHigh,
            _ => throw new InvalidOperationException($"Unknown reasoning effort '{effort}'. Valid values: none, low, medium, high, extra-high.")
        };
        return new ReasoningOptions { Effort = parsed };
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

    private sealed class OutputLoopDetector
    {
        private readonly Dictionary<string, int> _lineCounts = new(StringComparer.Ordinal);
        private readonly Queue<string> _recentLines = [];
        private readonly StringBuilder _pendingLine = new();
        private readonly int _windowLines;
        private readonly int _maxDistinctLines;

        public OutputLoopDetector(int windowLines, int maxDistinctLines)
        {
            _windowLines = windowLines;
            _maxDistinctLines = maxDistinctLines;
        }

        public bool Observe(string text)
        {
            foreach (char ch in text)
            {
                if (ch == '\n')
                {
                    if (AddLine(_pendingLine.ToString()))
                    {
                        _pendingLine.Clear();
                        return true;
                    }

                    _pendingLine.Clear();
                    continue;
                }

                if (ch != '\r')
                {
                    _pendingLine.Append(ch);
                }
            }

            return false;
        }

        private bool AddLine(string line)
        {
            string normalized = NormalizeLine(line);
            _recentLines.Enqueue(normalized);
            _lineCounts[normalized] = _lineCounts.GetValueOrDefault(normalized) + 1;

            if (_recentLines.Count > _windowLines)
            {
                string removed = _recentLines.Dequeue();
                int count = _lineCounts[removed] - 1;
                if (count == 0)
                {
                    _lineCounts.Remove(removed);
                }
                else
                {
                    _lineCounts[removed] = count;
                }
            }

            return _recentLines.Count == _windowLines && _lineCounts.Count <= _maxDistinctLines;
        }

        private static string NormalizeLine(string line)
        {
            string trimmed = NormalizeWhitespace(line.Trim());
            if (trimmed.Length == 0)
            {
                return "<blank>";
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return "<fence>";
            }

            if (trimmed.Contains('╭', StringComparison.Ordinal) && trimmed.Contains('╮', StringComparison.Ordinal))
            {
                return "<box-top>";
            }

            if (trimmed.Contains('╰', StringComparison.Ordinal) && trimmed.Contains('╯', StringComparison.Ordinal))
            {
                return "<box-bottom>";
            }

            if (trimmed.StartsWith('│') && trimmed.EndsWith('│'))
            {
                string inner = NormalizeWhitespace(trimmed.Trim('│', ' ').Trim());
                return inner.Length == 0 ? "<box-empty>" : "<box-content>:" + inner;
            }

            return trimmed;
        }

        private static string NormalizeWhitespace(string value)
        {
            StringBuilder builder = new(value.Length);
            bool previousWasWhitespace = false;
            foreach (char ch in value)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                        previousWasWhitespace = true;
                    }

                    continue;
                }

                builder.Append(ch);
                previousWasWhitespace = false;
            }

            return builder.ToString().Trim();
        }
    }

    private sealed class RuntimeToolActivitySink : IToolActivitySink
    {
        private readonly ConcurrentQueue<AgentEvent> _events = new();

        public void ToolStarted(string toolName)
        {
            _events.Enqueue(new AgentEvent(
                AgentEventKind.ToolActivity,
                $"tool started: {toolName}",
                ToolActivity: new ToolActivityInfo(toolName, ToolActivityPhase.Started)));
        }

        public void ToolCompleted(string toolName, ToolResult result, TimeSpan duration)
        {
            string status = result.Succeeded ? "completed" : "failed";
            _events.Enqueue(new AgentEvent(
                AgentEventKind.ToolActivity,
                $"tool {status}: {toolName} ({duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms)",
                ToolActivity: new ToolActivityInfo(
                    toolName,
                    ToolActivityPhase.Completed,
                    Succeeded: result.Succeeded,
                    Duration: duration)));
        }

        public void ToolFailed(string toolName, Exception exception, TimeSpan duration)
        {
            _events.Enqueue(new AgentEvent(
                AgentEventKind.ToolActivity,
                $"tool exception: {toolName} ({duration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms) {exception.GetType().Name}",
                ToolActivity: new ToolActivityInfo(
                    toolName,
                    ToolActivityPhase.Failed,
                    Succeeded: false,
                    Duration: duration,
                    ExceptionTypeName: exception.GetType().Name)));
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
