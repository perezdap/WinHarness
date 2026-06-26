using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Providers;
using WinHarness.Tools;

namespace WinHarness.Runtime;

/// <summary>
/// Initial single-agent runtime implementation.
/// </summary>
public sealed class SingleAgentRuntime : IAgentRuntime
{
    private readonly IProviderFactory _providerFactory;
    private readonly IEnumerable<IToolProvider> _toolProviders;
    private readonly ILogger<SingleAgentRuntime> _logger;

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(IProviderFactory providerFactory, ILogger<SingleAgentRuntime> logger)
        : this(providerFactory, [], logger)
    {
    }

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(
        IProviderFactory providerFactory,
        IEnumerable<IToolProvider> toolProviders,
        ILogger<SingleAgentRuntime> logger)
    {
        _providerFactory = providerFactory;
        _toolProviders = toolProviders;
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

        ChatOptions? options = await CreateChatOptionsAsync(cancellationToken).ConfigureAwait(false);

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           request.Prompt,
                           options,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            string text = update.ToString();
            if (text.Length > 0)
            {
                yield return new AgentEvent(AgentEventKind.AssistantDelta, text);
            }
        }

        _logger.ProviderRequestCompleted(provider.ProviderId, provider.ModelId);
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

                tools.Add(new ToolAIFunctionAdapter(tool));
            }
        }

        return tools.Count == 0 ? null : new ChatOptions { Tools = tools };
    }
}
