using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ModelCapabilityResolverTests
{
    [TestMethod]
    public async Task Tier1EndpointFalseWinsOverOpenRouterTrue()
    {
        // Endpoint advertises Vision=false (authoritative); OpenRouter says
        // Vision=true. Tier 1 must win.
        StubOpenRouterCatalog openRouter = new(BuildIndex(
            new OpenRouterModelEntry("openai/gpt-4o", ["image"], ["tools", "structured_outputs"], ContextLength: 128000)));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("openai/gpt-4o", OwnedBy: "openai", Vision: false);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsFalse(caps.Vision, "endpoint false must override OpenRouter true");
        Assert.IsTrue(caps.ToolCalling, "endpoint silent + OpenRouter tools=true -> true");
        Assert.IsTrue(caps.StructuredOutput, "endpoint silent + OpenRouter structured_outputs=true -> true");
    }

    [TestMethod]
    public async Task Tier2FillsInWhenEndpointSilentSuffixMatch()
    {
        // Endpoint is silent (Vision=null); OpenRouter entry is keyed
        // "openai/gpt-4o-mini". The endpoint id "gpt-4o-mini" matches via the
        // suffix-after-slash rule.
        StubOpenRouterCatalog openRouter = new(BuildIndex(
            new OpenRouterModelEntry("openai/gpt-4o-mini", ["image"], ["tools"], ContextLength: 128000)));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("gpt-4o-mini", OwnedBy: null);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsTrue(caps.Vision, "Tier 2 should fill in Vision from OpenRouter input_modalities");
        Assert.IsTrue(caps.ToolCalling, "Tier 2 should fill in ToolCalling from OpenRouter supported_parameters");
    }

    [TestMethod]
    public async Task Tier2OfflineReturnsInferrerFallbackWithoutThrowing()
    {
        StubOpenRouterCatalog openRouter = new(index: null);
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("claude-3-opus", OwnedBy: null);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        // No OpenRouter; falls back to name heuristic. PromptCaching=true from
        // the claude-3 name heuristic.
        Assert.IsTrue(caps.PromptCaching);
        Assert.IsTrue(caps.Streaming);
        Assert.IsTrue(caps.ToolCalling);
    }

    [TestMethod]
    public async Task Tier2NoIdMatchReturnsFallback()
    {
        StubOpenRouterCatalog openRouter = new(BuildIndex(
            new OpenRouterModelEntry("openai/gpt-4o", ["image"], ["tools"], ContextLength: 128000)));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("some-unknown-model", OwnedBy: null);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsFalse(caps.Vision, "no id match -> fallback (inferrer says false for unknown id)");
        Assert.IsTrue(caps.ToolCalling, "unknown chat id -> ToolCalling=true from inferrer default");
    }

    [TestMethod]
    public async Task NonChatIdGuardShortCircuitsTier2()
    {
        // OpenRouter has an entry for an id that would match after suffix
        // strip, but the non-chat guard wins.
        StubOpenRouterCatalog openRouter = new(BuildIndex(
            new OpenRouterModelEntry("openai/text-embedding-3-small", [], ["tools"], ContextLength: 8191)));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("openai/text-embedding-3-small", OwnedBy: "openai");
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsFalse(caps.Streaming);
        Assert.IsFalse(caps.ToolCalling, "non-chat guard must win over OpenRouter tools=true");
        Assert.IsFalse(caps.Vision);
        Assert.IsFalse(caps.Reasoning);
    }

    [TestMethod]
    public async Task PromptCachingUnaffectedByTier2()
    {
        // OpenRouter has no PromptCaching signal; the resolver must keep the
        // inferrer's name-heuristic value.
        StubOpenRouterCatalog openRouter = new(BuildIndex(
            new OpenRouterModelEntry("anthropic/claude-3-opus", ["text"], ["tools", "structured_outputs"], ContextLength: 200000)));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("claude-3-opus", OwnedBy: null);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsTrue(caps.PromptCaching, "PromptCaching comes from name heuristic; Tier 2 does not touch it");
        Assert.IsTrue(caps.ToolCalling, "Tier 2 fills in ToolCalling");
        Assert.IsTrue(caps.StructuredOutput, "Tier 2 fills in StructuredOutput");
    }

    [TestMethod]
    public async Task ExactIdMatchResolvesBeforeSuffixMatch()
    {
        // Both "gpt-4o" and "openai/gpt-4o" exist in the index; the endpoint
        // id "gpt-4o" should exact-match the first, not suffix-match the
        // second.
        OpenRouterModelEntry exact = new("gpt-4o", ["image"], ["tools"], ContextLength: 128000);
        OpenRouterModelEntry withPrefix = new("openai/gpt-4o", [], [], ContextLength: 8000);
        StubOpenRouterCatalog openRouter = new(BuildIndex(exact, withPrefix));
        ModelCapabilityResolver resolver = new(new ModelCapabilityInferrer(), openRouter);

        CatalogModel model = new("gpt-4o", OwnedBy: null);
        ProviderCapabilities caps = await resolver.ResolveAsync(model, CancellationToken.None);

        Assert.IsTrue(caps.Vision, "exact match (Vision=true) should win over suffix match (Vision=null)");
        Assert.IsTrue(caps.ToolCalling);
    }

    private static IReadOnlyDictionary<string, OpenRouterModelEntry> BuildIndex(params OpenRouterModelEntry[] entries)
    {
        // Mimic the real OpenRouterModelCatalog, which indexes each entry
        // under both its full id and the suffix after the last '/'.
        Dictionary<string, OpenRouterModelEntry> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (OpenRouterModelEntry entry in entries)
        {
            index[entry.Id] = entry;
            int slash = entry.Id.LastIndexOf('/');
            if (slash >= 0 && slash < entry.Id.Length - 1)
            {
                index.TryAdd(entry.Id[(slash + 1)..], entry);
            }
        }

        return index;
    }

    private sealed class StubOpenRouterCatalog : IOpenRouterModelCatalog
    {
        private readonly IReadOnlyDictionary<string, OpenRouterModelEntry>? _index;

        public StubOpenRouterCatalog(IReadOnlyDictionary<string, OpenRouterModelEntry>? index)
        {
            _index = index;
        }

        public ValueTask<IReadOnlyDictionary<string, OpenRouterModelEntry>?> TryGetIndexAsync(
            CancellationToken cancellationToken)
        {
            return new(_index);
        }
    }
}
