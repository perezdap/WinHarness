using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ModelCapabilityInferrerTests
{
    private static readonly ModelCapabilityInferrer Inferrer = new();

    [TestMethod]
    public void EndpointToolCallingFalseWinsOverNameHeuristic()
    {
        // claude-3-opus would default ToolCalling=true via the non-chat guard,
        // but the endpoint's explicit false is authoritative.
        CatalogModel model = new("claude-3-opus", OwnedBy: null, ToolCalling: false);

        ProviderCapabilities caps = Inferrer.Infer(model);

        Assert.IsFalse(caps.ToolCalling, "endpoint false must override the non-chat default of true");
    }

    [TestMethod]
    public void NameHeuristicFillsInWhenEndpointSilent()
    {
        CatalogModel claude = new("claude-3-opus", OwnedBy: null);
        CatalogModel gpt4o = new("gpt-4o-mini", OwnedBy: null);

        ProviderCapabilities claudeCaps = Inferrer.Infer(claude);
        ProviderCapabilities gpt4oCaps = Inferrer.Infer(gpt4o);

        Assert.IsTrue(claudeCaps.PromptCaching, "claude-3 name heuristic -> PromptCaching=true");
        Assert.IsTrue(gpt4oCaps.StructuredOutput, "gpt-4o name heuristic -> StructuredOutput=true");
    }

    [TestMethod]
    public void EndpointStructuredOutputFalseSuppressesNameHeuristic()
    {
        // gpt-4o would default StructuredOutput=true via the name heuristic,
        // but the endpoint's explicit false is authoritative.
        CatalogModel model = new("gpt-4o", OwnedBy: null, StructuredOutput: false);

        ProviderCapabilities caps = Inferrer.Infer(model);

        Assert.IsFalse(caps.StructuredOutput, "endpoint false must override the name heuristic");
    }

    [TestMethod]
    public void VisionIsEndpointOnlyFalseWhenSilent()
    {
        CatalogModel silent = new("gpt-4o", OwnedBy: null);
        CatalogModel advertised = new("gpt-4o", OwnedBy: null, Vision: true);

        ProviderCapabilities silentCaps = Inferrer.Infer(silent);
        ProviderCapabilities advertisedCaps = Inferrer.Infer(advertised);

        Assert.IsFalse(silentCaps.Vision, "Vision is endpoint-only; silent -> false (no name guessing)");
        Assert.IsTrue(advertisedCaps.Vision, "endpoint Vision=true wins");
    }

    [TestMethod]
    public void ReasoningUsesEndpointBoolThenEffortsThenFalse()
    {
        CatalogModel explicitFalse = new("gpt-4o", OwnedBy: null, Reasoning: false);
        CatalogModel explicitTrue = new("gpt-4o", OwnedBy: null, Reasoning: true);
        CatalogModel effortsOnly = new("gpt-4o", OwnedBy: null, SupportedReasoningEfforts: ["low", "high"]);
        CatalogModel silent = new("gpt-4o", OwnedBy: null);

        Assert.IsFalse(Inferrer.Infer(explicitFalse).Reasoning, "endpoint Reasoning=false is authoritative");
        Assert.IsTrue(Inferrer.Infer(explicitTrue).Reasoning, "endpoint Reasoning=true wins");
        Assert.IsTrue(Inferrer.Infer(effortsOnly).Reasoning, "non-empty efforts imply Reasoning=true when endpoint bool is silent");
        Assert.IsFalse(Inferrer.Infer(silent).Reasoning, "silent + no efforts -> false (no name guessing)");
    }

    [TestMethod]
    public void PromptCachingUsesEndpointThenNameHeuristic()
    {
        CatalogModel endpointTrue = new("llama-3.1-405b", OwnedBy: null, PromptCaching: true);
        CatalogModel endpointFalse = new("claude-3-opus", OwnedBy: null, PromptCaching: false);
        CatalogModel nameHeuristic = new("claude-3-opus", OwnedBy: null);
        CatalogModel silent = new("llama-3.1-405b", OwnedBy: null);

        Assert.IsTrue(Inferrer.Infer(endpointTrue).PromptCaching, "endpoint PromptCaching=true wins even without name match");
        Assert.IsFalse(Inferrer.Infer(endpointFalse).PromptCaching, "endpoint PromptCaching=false is authoritative even with name match");
        Assert.IsTrue(Inferrer.Infer(nameHeuristic).PromptCaching, "claude-3 name heuristic -> true");
        Assert.IsFalse(Inferrer.Infer(silent).PromptCaching, "silent + no name match -> false");
    }

    [TestMethod]
    public void NonChatIdGuardForcesAllCapabilitiesFalse()
    {
        string[] nonChatIds = ["text-embedding-3-small", "text-moderation-latest", "whisper-1", "tts-1", "dall-e-3", "rerank-1"];

        foreach (string id in nonChatIds)
        {
            CatalogModel model = new(id, OwnedBy: null);
            ProviderCapabilities caps = Inferrer.Infer(model);

            Assert.IsFalse(caps.Streaming, $"{id}: Streaming must be false");
            Assert.IsFalse(caps.ToolCalling, $"{id}: ToolCalling must be false");
            Assert.IsFalse(caps.Vision, $"{id}: Vision must be false");
            Assert.IsFalse(caps.PromptCaching, $"{id}: PromptCaching must be false");
            Assert.IsFalse(caps.StructuredOutput, $"{id}: StructuredOutput must be false");
            Assert.IsFalse(caps.Reasoning, $"{id}: Reasoning must be false");
        }
    }

    [TestMethod]
    public void NonChatIdGuardOverridesEvenEndpointTrueSignals()
    {
        // The guard wins over endpoint-advertised values to preserve the
        // original wizard behavior for non-chat ids.
        CatalogModel model = new("text-embedding-3-small", OwnedBy: null, Vision: true, ToolCalling: true);

        ProviderCapabilities caps = Inferrer.Infer(model);

        Assert.IsFalse(caps.Streaming);
        Assert.IsFalse(caps.ToolCalling);
        Assert.IsFalse(caps.Vision);
    }

    [TestMethod]
    public void BareOpenAiShapeYieldsWizardDefaults()
    {
        // Bare OpenAI /models returns only id+owned_by; all extension fields
        // are null. This matches the pre-refactor wizard defaults.
        CatalogModel model = new("gpt-4.1", OwnedBy: "openai");

        ProviderCapabilities caps = Inferrer.Infer(model);

        Assert.IsTrue(caps.Streaming);
        Assert.IsTrue(caps.ToolCalling);
        Assert.IsFalse(caps.Vision);
        Assert.IsFalse(caps.PromptCaching);
        Assert.IsFalse(caps.StructuredOutput);
        Assert.IsFalse(caps.Reasoning);
    }
}
