using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class OpenAiCompatibleModelCatalogTests
{
    [TestMethod]
    public async Task ParsesSortsAndDeduplicatesModels()
    {
        StubHandler handler = new(HttpStatusCode.OK, """
            {"object":"list","data":[
                {"id":"gpt-4.1","owned_by":"openai"},
                {"id":"gpt-3.5-turbo","owned_by":"openai"},
                {"id":"gpt-4.1","owned_by":"openai"},
                {"id":"","owned_by":"openai"}
            ]}
            """);
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
            "https://api.openai.com/v1",
            "sk-test",
            CancellationToken.None);

        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("gpt-3.5-turbo", models[0].Id);
        Assert.AreEqual("gpt-4.1", models[1].Id);
        Assert.AreEqual("https://api.openai.com/v1/models", handler.LastRequestUri?.ToString());
        Assert.AreEqual("Bearer sk-test", handler.LastAuthorization);
    }

    [TestMethod]
    public async Task OmitsAuthorizationWhenNoApiKey()
    {
        StubHandler handler = new(HttpStatusCode.OK, """{"data":[{"id":"local"}]}""");
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        await catalog.ListModelsAsync("http://localhost:11434/v1", apiKey: null, CancellationToken.None);

        Assert.IsNull(handler.LastAuthorization);
    }

    [TestMethod]
    public async Task ThrowsOnErrorStatus()
    {
        StubHandler handler = new(HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await catalog.ListModelsAsync("https://api.openai.com/v1", "sk-bad", CancellationToken.None));

        StringAssert.Contains(exception.Message, "401");
    }

    [TestMethod]
    public async Task ReturnsEmptyWhenNoData()
    {
        StubHandler handler = new(HttpStatusCode.OK, """{"object":"list","data":[]}""");
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
            "https://api.openai.com/v1",
            "sk-test",
            CancellationToken.None);

        Assert.AreEqual(0, models.Count);
    }

    [TestMethod]
    public async Task ParsesVeniceFixtureAndMapsModelSpecCapabilities()
    {
        string fixture = await LoadFixtureAsync("venice-models.json");
        StubHandler handler = new(HttpStatusCode.OK, fixture);
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
            "https://api.venice.ai/api/v1",
            "sk-test",
            CancellationToken.None);

        // Fixture has 4 models; parser sorts by id.
        Assert.AreEqual(4, models.Count);

        CatalogModel visionReasoning = models.Single(m => m.Id == "z-ai-glm-5v-turbo");
        Assert.IsTrue(visionReasoning.Vision, "supportsVision=true should map to Vision=true");
        Assert.IsTrue(visionReasoning.Reasoning, "supportsReasoning=true should map to Reasoning=true");
        Assert.IsTrue(visionReasoning.ToolCalling, "supportsFunctionCalling=true should map to ToolCalling=true");
        Assert.IsTrue(visionReasoning.StructuredOutput, "supportsResponseSchema=true should map to StructuredOutput=true");
        Assert.IsTrue(visionReasoning.PromptCaching, "pricing.cache_input present should map to PromptCaching=true");
        Assert.AreEqual(128000, visionReasoning.ContextWindow, "context_length should round-trip");
        CollectionAssert.AreEqual(
            new[] { "none", "low", "medium", "high" },
            visionReasoning.SupportedReasoningEfforts?.ToArray() ?? Array.Empty<string>());

        CatalogModel reasoningNoVision = models.Single(m => m.Id == "zai-org-glm-5-1");
        Assert.IsFalse(reasoningNoVision.Vision!.Value, "supportsVision=false is authoritative-false");
        Assert.IsTrue(reasoningNoVision.Reasoning, "supportsReasoning=true");
        Assert.IsTrue(reasoningNoVision.ToolCalling);
        Assert.IsTrue(reasoningNoVision.StructuredOutput);
        Assert.IsTrue(reasoningNoVision.PromptCaching);

        // Venice explicitly says supportsReasoning=true but omits
        // reasoningEffortOptions; Reasoning=true comes from the direct signal,
        // SupportedReasoningEfforts stays null.
        CatalogModel reasoningNoEfforts = models.Single(m => m.Id == "olafangensan-glm-4.7-flash-heretic");
        Assert.IsTrue(reasoningNoEfforts.Reasoning, "supportsReasoning=true with no effort options still yields Reasoning=true");
        Assert.IsNull(reasoningNoEfforts.SupportedReasoningEfforts, "reasoningEffortOptions absent -> null");
        Assert.IsTrue(reasoningNoEfforts.ToolCalling);
        Assert.IsTrue(reasoningNoEfforts.StructuredOutput);
        Assert.IsNull(reasoningNoEfforts.PromptCaching, "no cache_input -> PromptCaching null (falls to name heuristic)");

        CatalogModel plain = models.Single(m => m.Id == "hermes-3-llama-3.1-405b");
        Assert.IsFalse(plain.Vision!.Value, "supportsVision=false");
        Assert.IsFalse(plain.Reasoning!.Value, "supportsReasoning=false is authoritative-false");
        Assert.IsFalse(plain.ToolCalling!.Value, "supportsFunctionCalling=false");
        Assert.IsFalse(plain.StructuredOutput!.Value, "supportsResponseSchema=false");
        Assert.IsNull(plain.PromptCaching, "no cache_input -> null");
    }

    [TestMethod]
    public async Task ParsesOllamaFixtureWithMinimalShapeAndNullExtensionFields()
    {
        string fixture = await LoadFixtureAsync("ollama-models.json");
        StubHandler handler = new(HttpStatusCode.OK, fixture);
        OpenAiCompatibleModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
            "http://localhost:11434/v1",
            apiKey: null,
            CancellationToken.None);

        Assert.AreEqual(3, models.Count);
        foreach (CatalogModel model in models)
        {
            Assert.IsNotNull(model.Id);
            Assert.AreEqual("library", model.OwnedBy);
            // Ollama /v1/models is minimal: no architecture, no model_spec, no
            // supported_parameters. All extension fields must be null so the
            // resolver falls through to Tier 2 (OpenRouter) and Tier 3 (name
            // heuristic).
            Assert.IsNull(model.ContextWindow, $"{model.Id}: context window should be null");
            Assert.IsNull(model.Vision, $"{model.Id}: Vision should be null");
            Assert.IsNull(model.Reasoning, $"{model.Id}: Reasoning should be null");
            Assert.IsNull(model.ToolCalling, $"{model.Id}: ToolCalling should be null");
            Assert.IsNull(model.StructuredOutput, $"{model.Id}: StructuredOutput should be null");
            Assert.IsNull(model.PromptCaching, $"{model.Id}: PromptCaching should be null");
            Assert.IsNull(model.SupportedReasoningEfforts, $"{model.Id}: SupportedReasoningEfforts should be null");
        }
    }

    private static async Task<string> LoadFixtureAsync(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", name);
        return await File.ReadAllTextAsync(path, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public Uri? LastRequestUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
