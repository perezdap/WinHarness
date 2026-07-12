using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class AnthropicOAuthFlowTests
{
    [TestMethod]
    public void CreatePkceSessionBuildsAuthorizeUrl()
    {
        AnthropicOAuthFlow flow = new(new HttpClient(new FakeHandler()));
        AnthropicPkceSession session = flow.CreatePkceSession();

        StringAssert.StartsWith(session.AuthorizeUrl, AnthropicOAuthFlow.AuthorizeUrl);
        StringAssert.Contains(session.AuthorizeUrl, "code_challenge_method=S256");
        StringAssert.Contains(session.AuthorizeUrl, Uri.EscapeDataString(AnthropicOAuthFlow.RedirectUri));
        Assert.AreEqual(session.Verifier, session.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.Challenge));
    }

    [TestMethod]
    public void ParseAuthorizationInputAcceptsRedirectUrl()
    {
        AnthropicPkceSession session = new("verifier", "challenge", "verifier", "https://example");
        string input = $"{AnthropicOAuthFlow.RedirectUri}?code=abc123&state=verifier";

        AnthropicCallbackResult result = AnthropicOAuthFlow.ParseAuthorizationInput(input, session);

        Assert.AreEqual("abc123", result.Code);
        Assert.AreEqual("verifier", result.State);
    }

    [TestMethod]
    public void ParseAuthorizationInputAcceptsHashForm()
    {
        AnthropicPkceSession session = new("verifier", "challenge", "verifier", "https://example");

        AnthropicCallbackResult result = AnthropicOAuthFlow.ParseAuthorizationInput("code1#verifier", session);

        Assert.AreEqual("code1", result.Code);
        Assert.AreEqual("verifier", result.State);
    }

    [TestMethod]
    public void ParseAuthorizationInputRejectsStateMismatch()
    {
        AnthropicPkceSession session = new("verifier", "challenge", "verifier", "https://example");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => AnthropicOAuthFlow.ParseAuthorizationInput(
                $"{AnthropicOAuthFlow.RedirectUri}?code=abc&state=other",
                session));
    }

    [TestMethod]
    public async Task ExchangeCodeProducesTokenSet()
    {
        FakeHandler handler = new(
            """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,"scope":"user:inference"}""");
        AnthropicOAuthFlow flow = new(new HttpClient(handler));
        AnthropicPkceSession session = new("verifier", "challenge", "verifier", "https://example");
        AnthropicCallbackResult callback = new("auth-code", "verifier");

        OAuthTokenSet tokens = await flow.ExchangeCodeAsync(session, callback, CancellationToken.None);

        Assert.AreEqual("access-1", tokens.AccessToken);
        Assert.AreEqual("refresh-1", tokens.RefreshToken);
        Assert.AreEqual(AnthropicOAuthFlow.DefaultBaseUrl, tokens.BaseUrl);
        Assert.IsTrue(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30));
        StringAssert.Contains(handler.LastBody!, "authorization_code");
        StringAssert.Contains(handler.LastBody!, "auth-code");
    }

    [TestMethod]
    public async Task RefreshProducesRotatedTokensAndPreservesMissingRefresh()
    {
        FakeHandler handler = new(
            """{"access_token":"access-2","expires_in":1800}""");
        AnthropicOAuthFlow flow = new(new HttpClient(handler));
        OAuthTokenSet current = new("old", "refresh-keep", DateTimeOffset.UtcNow);

        OAuthTokenSet tokens = await flow.RefreshAsync(current, CancellationToken.None);

        Assert.AreEqual("access-2", tokens.AccessToken);
        Assert.AreEqual("refresh-keep", tokens.RefreshToken);
        StringAssert.Contains(handler.LastBody!, "refresh_token");
    }

    [TestMethod]
    public async Task RefreshWithoutStoredTokenIsActionable()
    {
        AnthropicOAuthFlow flow = new(new HttpClient(new FakeHandler()));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await flow.RefreshAsync(
                new OAuthTokenSet("bearer", RefreshToken: null, ExpiresAt: null),
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "winharness login --provider anthropic");
    }

    [TestMethod]
    public void DefaultModelsAreSeeded()
    {
        Assert.IsTrue(AnthropicOAuthFlow.DefaultModels.Count >= 2);
        Assert.IsTrue(AnthropicOAuthFlow.DefaultModels.Any(model => model.Id.Contains("sonnet", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public FakeHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            string body = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
