namespace WinHarness.Providers;

/// <summary>
/// Discovers the models an OpenAI-compatible endpoint advertises through its
/// <c>GET /models</c> route. This is read-only remote metadata; it does not
/// touch WinHarness configuration. Callers (the setup wizard, the
/// <c>models discover</c> command) decide which discovered ids to persist.
/// </summary>
public interface IModelCatalog
{
    /// <summary>
    /// Lists the model ids advertised by an endpoint. <paramref name="baseUrl"/>
    /// is the OpenAI-compatible base (for example <c>https://api.openai.com/v1</c>);
    /// the catalog appends <c>/models</c>. <paramref name="apiKey"/> may be null
    /// for keyless local endpoints. Results are sorted and de-duplicated.
    /// </summary>
    ValueTask<IReadOnlyList<CatalogModel>> ListModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// A model advertised by an OpenAI-compatible endpoint. Nullable booleans
/// mean "endpoint did not say"; a present <c>false</c> is authoritative-false.
/// The inference layer (<c>IModelCapabilityResolver</c>) falls back to name
/// heuristics and OpenRouter's public catalog only when an endpoint is silent
/// (<c>null</c>) on a given capability.
/// </summary>
public sealed record CatalogModel(
    string Id,
    string? OwnedBy,
    int? ContextWindow = null,
    bool? Vision = null,
    System.Collections.Generic.List<string>? SupportedReasoningEfforts = null,
    bool? Reasoning = null,
    bool? ToolCalling = null,
    bool? StructuredOutput = null,
    bool? PromptCaching = null);
