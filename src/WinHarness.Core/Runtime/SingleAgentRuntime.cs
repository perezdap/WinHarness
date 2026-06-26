using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using WinHarness.Providers;

namespace WinHarness.Runtime;

/// <summary>
/// Initial single-agent runtime implementation.
/// </summary>
public sealed class SingleAgentRuntime : IAgentRuntime
{
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger<SingleAgentRuntime> _logger;

    /// <summary>
    /// Creates a runtime.
    /// </summary>
    public SingleAgentRuntime(IProviderFactory providerFactory, ILogger<SingleAgentRuntime> logger)
    {
        _providerFactory = providerFactory;
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

        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                           request.Prompt,
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
}
