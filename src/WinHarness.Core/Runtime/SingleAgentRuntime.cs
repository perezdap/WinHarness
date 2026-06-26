using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Diagnostics;
using WinHarness.Providers;
using WinHarness.Tools;

namespace WinHarness.Runtime;

/// <summary>
/// Initial single-agent runtime implementation.
/// </summary>
public sealed class SingleAgentRuntime : IAgentRuntime
{
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
        IChatProvider provider = _providerFactory.Create(request.ProviderId, request.ModelId);
        using IChatClient client = provider.CreateChatClient();

        _logger.ProviderRequestStarting(provider.ProviderId, provider.ModelId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        await WriteProviderDiagnosticAsync(
            "provider.started",
            provider,
            stopwatch,
            cancellationToken).ConfigureAwait(false);

        ChatOptions? options = await CreateChatOptionsAsync(cancellationToken).ConfigureAwait(false);

        await using IAsyncEnumerator<ChatResponseUpdate> updates = client.GetStreamingResponseAsync(
                request.Prompt,
                options,
                cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;
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
                throw;
            }

            string text = update.ToString();
            if (text.Length > 0)
            {
                yield return new AgentEvent(AgentEventKind.AssistantDelta, text);
            }
        }

        _logger.ProviderRequestCompleted(provider.ProviderId, provider.ModelId);
        await WriteProviderDiagnosticAsync(
            "provider.completed",
            provider,
            stopwatch,
            cancellationToken).ConfigureAwait(false);
        yield return new AgentEvent(AgentEventKind.Completed, "completed");
    }

    private async ValueTask<ChatOptions?> CreateChatOptionsAsync(CancellationToken cancellationToken)
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

                tools.Add(new ToolAIFunctionAdapter(tool, _diagnosticSink));
            }
        }

        return tools.Count == 0 ? null : new ChatOptions { Tools = tools };
    }

    private async ValueTask WriteProviderDiagnosticAsync(
        string eventName,
        IChatProvider provider,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        Dictionary<string, string> properties = new()
        {
            ["provider.id"] = provider.ProviderId,
            ["model.id"] = provider.ModelId,
            ["provider.duration_ms"] = stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
        };

        if (exception is not null)
        {
            properties["exception.type"] = exception.GetType().FullName ?? exception.GetType().Name;
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

    private sealed class NullDiagnosticSink : IDiagnosticSink
    {
        public ValueTask WriteAsync(DiagnosticRecord record, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }
}
