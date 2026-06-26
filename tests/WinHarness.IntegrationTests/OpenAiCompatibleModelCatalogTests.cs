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
