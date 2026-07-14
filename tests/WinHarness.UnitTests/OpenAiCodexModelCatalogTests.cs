using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class OpenAiCodexModelCatalogTests
{
    [TestMethod]
    public void ParsesVisibleModelsAndSkipsHiddenModels()
    {
        RecordingHandler handler = new("""
            {
              "models": [
                {
                  "slug": "gpt-codex-visible",
                  "visibility": "list",
                  "context_window": 300000,
                  "input_modalities": ["text", "image"],
                  "supported_reasoning_levels": [{"effort": "low"}, {"effort": "high"}]
                },
                {"slug": "gpt-codex-hidden", "visibility": "hide"}
              ]
            }
            """);
        using HttpClient http = new(handler);
        OpenAiCodexModelCatalog catalog = new(http);

        IReadOnlyList<CatalogModel> models = catalog
            .ListModelsAsync("https://chatgpt.com/backend-api", "access-token", "account-id", CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("gpt-codex-visible", models[0].Id);
        Assert.AreEqual(300000, models[0].ContextWindow);
        Assert.IsTrue(models[0].Vision);
        CollectionAssert.AreEqual(new[] { "low", "high" }, models[0].SupportedReasoningEfforts);
        Assert.IsTrue(models[0].Reasoning);
    }

    [TestMethod]
    public async Task SendsCodexAccountAndVersionHeaders()
    {
        RecordingHandler handler = new();
        using HttpClient http = new(handler);
        OpenAiCodexModelCatalog catalog = new(http);

        IReadOnlyList<CatalogModel> models = await catalog.ListModelsAsync(
            "https://chatgpt.com/backend-api",
            "access-token",
            "account-id",
            CancellationToken.None);

        Assert.AreEqual(0, models.Count);
        Assert.AreEqual("https://chatgpt.com/backend-api/codex/models?client_version=0.0.0", handler.Request!.RequestUri!.ToString());
        Assert.AreEqual("Bearer access-token", handler.Request.Headers.Authorization!.ToString());
        Assert.AreEqual("account-id", handler.Request.Headers.GetValues("ChatGPT-Account-ID").Single());
        Assert.IsFalse(handler.Request.Headers.Contains("originator"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public RecordingHandler(string body = "{\"models\":[]}") => _body = body;

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            });
        }
    }
}
