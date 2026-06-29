namespace WinHarness.Providers;

/// <summary>
/// Orchestrates capability resolution across three tiers:
/// <list type="number">
/// <item><description>Tier 1: endpoint-advertised values from the
/// <see cref="CatalogModel"/> (authoritative, including present
/// <c>false</c>).</description></item>
/// <item><description>Tier 2: OpenRouter's public catalog cross-referenced by
/// model id (best-effort; fills in only when the endpoint was silent and only
/// on a positive signal).</description></item>
/// <item><description>Tier 3+4: name heuristics and conservative defaults from
/// <see cref="IModelCapabilityInferrer"/> (no network).</description></item>
/// </list>
/// The non-chat id guard (<c>embedding</c>/<c>moderation</c>/etc.) short-circuits
/// to the inferrer fallback — Tier 2 never overrides a non-chat model.
/// </summary>
public interface IModelCapabilityResolver
{
    /// <summary>
    /// Resolves capabilities for a single discovered model.
    /// </summary>
    ValueTask<ProviderCapabilities> ResolveAsync(
        CatalogModel endpointModel,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default implementation.
/// </summary>
public sealed class ModelCapabilityResolver : IModelCapabilityResolver
{
    private readonly IModelCapabilityInferrer _inferrer;
    private readonly IOpenRouterModelCatalog _openRouter;

    public ModelCapabilityResolver(IModelCapabilityInferrer inferrer, IOpenRouterModelCatalog openRouter)
    {
        _inferrer = inferrer;
        _openRouter = openRouter;
    }

    /// <inheritdoc />
    public async ValueTask<ProviderCapabilities> ResolveAsync(
        CatalogModel endpointModel,
        CancellationToken cancellationToken)
    {
        ProviderCapabilities fallback = _inferrer.Infer(endpointModel);

        string id = endpointModel.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id) || ModelCapabilityInferrer.IsNonChatId(id.ToLowerInvariant()))
        {
            return fallback;
        }

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await _openRouter
            .TryGetIndexAsync(cancellationToken)
            .ConfigureAwait(false);

        if (index is null || index.Count == 0)
        {
            return fallback;
        }

        if (!TryMatchById(index, id, out OpenRouterModelEntry? entry) || entry is null)
        {
            return fallback;
        }

        // Override the fallback only when the endpoint was silent (null) AND
        // OpenRouter has a positive signal (true). OpenRouter derived bools are
        // true/null (never false), so absence does not downgrade the fallback.
        bool vision = endpointModel.Vision ?? entry.Vision ?? fallback.Vision;
        bool toolCalling = endpointModel.ToolCalling ?? entry.ToolCalling ?? fallback.ToolCalling;
        bool structuredOutput = endpointModel.StructuredOutput ?? entry.StructuredOutput ?? fallback.StructuredOutput;
        bool reasoning = endpointModel.Reasoning
            ?? ((endpointModel.SupportedReasoningEfforts is { Count: > 0 }) ? true : (entry.Reasoning ?? fallback.Reasoning));
        bool promptCaching = endpointModel.PromptCaching ?? fallback.PromptCaching;
        bool streaming = fallback.Streaming;

        return new ProviderCapabilities(
            Streaming: streaming,
            ToolCalling: toolCalling,
            Vision: vision,
            PromptCaching: promptCaching,
            StructuredOutput: structuredOutput,
            Reasoning: reasoning);
    }

    private static bool TryMatchById(
        IReadOnlyDictionary<string, OpenRouterModelEntry> index,
        string id,
        out OpenRouterModelEntry? entry)
    {
        if (index.TryGetValue(id, out entry))
        {
            return true;
        }

        // Suffix match: try the part after the last '/' so "gpt-4o-mini" matches
        // OpenRouter's "openai/gpt-4o-mini". Ollama tags like "qwen2.5-coder:latest"
        // won't match OpenRouter ids; the resolver returns fallback for those.
        int slash = id.LastIndexOf('/');
        if (slash >= 0 && slash < id.Length - 1)
        {
            string suffix = id[(slash + 1)..];
            if (index.TryGetValue(suffix, out entry))
            {
                return true;
            }
        }

        entry = null;
        return false;
    }
}
