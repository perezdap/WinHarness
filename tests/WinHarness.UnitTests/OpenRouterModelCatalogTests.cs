using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class OpenRouterModelCatalogTests
{
    private const string SampleJson = """
        {
          "data": [
            {
              "id": "openai/gpt-4o",
              "context_length": 128000,
              "architecture": { "input_modalities": ["text", "image"] },
              "supported_parameters": ["tools", "tool_choice", "structured_outputs", "reasoning"],
              "top_provider": { "context_length": 128000 }
            },
            {
              "id": "meta-llama/llama-3.1-405b-instruct",
              "context_length": 131072,
              "architecture": { "input_modalities": ["text"] },
              "supported_parameters": ["tools"]
            }
          ]
        }
        """;

    [TestMethod]
    public async Task ParsesIndexAndDerivesCapabilityBooleans()
    {
        StubHandler handler = new(HttpStatusCode.OK, SampleJson);
        OpenRouterModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await catalog.TryGetIndexAsync(CancellationToken.None);

        Assert.IsNotNull(index);
        // The catalog indexes each model under both its full id and the
        // suffix after the last '/' so endpoint ids like "gpt-4o" match
        // OpenRouter's "openai/gpt-4o" via a single lookup. 2 models × 2
        // keys each = 4 index entries.
        Assert.AreEqual(4, index.Count);

        Assert.IsTrue(index.TryGetValue("openai/gpt-4o", out OpenRouterModelEntry? gpt4o));
        Assert.IsNotNull(gpt4o);
        Assert.IsTrue(gpt4o.Vision, "input_modalities contains image -> Vision=true");
        Assert.IsTrue(gpt4o.ToolCalling, "supported_parameters contains tools -> ToolCalling=true");
        Assert.IsTrue(gpt4o.StructuredOutput, "supported_parameters contains structured_outputs -> StructuredOutput=true");
        Assert.IsTrue(gpt4o.Reasoning, "supported_parameters contains reasoning -> Reasoning=true");
        Assert.AreEqual(128000, gpt4o.ContextLength);

        Assert.IsTrue(index.TryGetValue("meta-llama/llama-3.1-405b-instruct", out OpenRouterModelEntry? llama));
        Assert.IsNotNull(llama);
        Assert.IsNull(llama.Vision, "no image in input_modalities -> Vision=null (not false)");
        Assert.IsTrue(llama.ToolCalling);
        Assert.IsNull(llama.StructuredOutput, "no structured_outputs -> null (absence is not unsupported)");
        Assert.IsNull(llama.Reasoning);
    }

    [TestMethod]
    public async Task IndexIsCaseInsensitive()
    {
        StubHandler handler = new(HttpStatusCode.OK, SampleJson);
        OpenRouterModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await catalog.TryGetIndexAsync(CancellationToken.None);

        Assert.IsNotNull(index);
        Assert.IsTrue(index.ContainsKey("OpenAI/GPT-4O"), "index must be case-insensitive for id-matching");
    }

    [TestMethod]
    public async Task ReturnsNullOnNonSuccessStatusCode()
    {
        StubHandler handler = new(HttpStatusCode.InternalServerError, """{"error":"oops"}""");
        OpenRouterModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await catalog.TryGetIndexAsync(CancellationToken.None);

        Assert.IsNull(index, "non-2xx must return null, not throw");
    }

    [TestMethod]
    public async Task ReturnsNullOnUnreachableEndpoint()
    {
        // A handler that throws on SendAsync simulates a transport failure
        // (DNS, connection refused, timeout). The catalog must swallow it and
        // return null rather than leaking the exception.
        ThrowHandler handler = new();
        OpenRouterModelCatalog catalog = new(new HttpClient(handler), TimeSpan.FromSeconds(5));

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await catalog.TryGetIndexAsync(CancellationToken.None);

        Assert.IsNull(index, "transport failure must return null, not throw");
    }

    [TestMethod]
    public async Task ReturnsNullOnUnparseableBody()
    {
        StubHandler handler = new(HttpStatusCode.OK, "not json");
        OpenRouterModelCatalog catalog = new(new HttpClient(handler));

        IReadOnlyDictionary<string, OpenRouterModelEntry>? index = await catalog.TryGetIndexAsync(CancellationToken.None);

        Assert.IsNull(index, "parse failure must return null, not throw");
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("simulated transport failure");
        }
    }
}
