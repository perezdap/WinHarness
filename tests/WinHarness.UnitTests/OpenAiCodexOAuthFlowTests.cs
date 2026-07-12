using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class OpenAiCodexOAuthFlowTests
{
    [TestMethod]
    public void CreatePkceSessionBuildsAuthorizeUrl()
    {
        OpenAiCodexOAuthFlow flow = new(new HttpClient(new FakeHandler()));
        OpenAiCodexPkceSession session = flow.CreatePkceSession();

        StringAssert.StartsWith(session.AuthorizeUrl, OpenAiCodexOAuthFlow.AuthorizeUrl);
        StringAssert.Contains(session.AuthorizeUrl, "code_challenge_method=S256");
        StringAssert.Contains(session.AuthorizeUrl, Uri.EscapeDataString(OpenAiCodexOAuthFlow.RedirectUri));
        StringAssert.Contains(session.AuthorizeUrl, "originator=winharness");
        StringAssert.Contains(session.AuthorizeUrl, "codex_cli_simplified_flow=true");
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.Challenge));
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.State));
    }

    [TestMethod]
    public void ParseAuthorizationInputAcceptsRedirectUrl()
    {
        OpenAiCodexPkceSession session = new("verifier", "challenge", "state-1", "https://example");
        string input = $"{OpenAiCodexOAuthFlow.RedirectUri}?code=abc123&state=state-1";

        OpenAiCodexCallbackResult result = OpenAiCodexOAuthFlow.ParseAuthorizationInput(input, session);

        Assert.AreEqual("abc123", result.Code);
        Assert.AreEqual("state-1", result.State);
    }

    [TestMethod]
    public void ParseAuthorizationInputAcceptsHashForm()
    {
        OpenAiCodexPkceSession session = new("verifier", "challenge", "state-1", "https://example");

        OpenAiCodexCallbackResult result = OpenAiCodexOAuthFlow.ParseAuthorizationInput("code1#state-1", session);

        Assert.AreEqual("code1", result.Code);
        Assert.AreEqual("state-1", result.State);
    }

    [TestMethod]
    public void ParseAuthorizationInputRejectsStateMismatch()
    {
        OpenAiCodexPkceSession session = new("verifier", "challenge", "state-1", "https://example");

        Assert.ThrowsExactly<InvalidOperationException>(
            () => OpenAiCodexOAuthFlow.ParseAuthorizationInput(
                $"{OpenAiCodexOAuthFlow.RedirectUri}?code=abc&state=other",
                session));
    }

    [TestMethod]
    public void ExtractAccountIdReadsJwtClaim()
    {
        string token = CreateJwtWithAccount("acct-42");
        Assert.AreEqual("acct-42", OpenAiCodexOAuthFlow.ExtractAccountId(token));
    }

    [TestMethod]
    public async Task ExchangeCodeProducesTokenSetWithAccountId()
    {
        string access = CreateJwtWithAccount("acct-1");
        string body = "{\"access_token\":\"" + access + "\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}";
        FakeHandler handler = new(body);
        OpenAiCodexOAuthFlow flow = new(new HttpClient(handler));
        OpenAiCodexPkceSession session = new("verifier", "challenge", "state-1", "https://example");
        OpenAiCodexCallbackResult callback = new("auth-code", "state-1");

        OAuthTokenSet tokens = await flow.ExchangeCodeAsync(session, callback, CancellationToken.None);

        Assert.AreEqual(access, tokens.AccessToken);
        Assert.AreEqual("refresh-1", tokens.RefreshToken);
        Assert.AreEqual("acct-1", tokens.AccountId);
        Assert.AreEqual(OpenAiCodexOAuthFlow.DefaultBaseUrl, tokens.BaseUrl);
        Assert.IsTrue(tokens.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(30));
        StringAssert.Contains(handler.LastBody!, "authorization_code");
        StringAssert.Contains(handler.LastBody!, "auth-code");
    }

    [TestMethod]
    public async Task RefreshProducesRotatedTokens()
    {
        string access = CreateJwtWithAccount("acct-new");
        string body = "{\"access_token\":\"" + access + "\",\"refresh_token\":\"refresh-new\",\"expires_in\":1800}";
        FakeHandler handler = new(body);
        OpenAiCodexOAuthFlow flow = new(new HttpClient(handler));
        OAuthTokenSet current = new("old", "refresh-keep", DateTimeOffset.UtcNow, AccountId: "acct-old");

        OAuthTokenSet tokens = await flow.RefreshAsync(current, CancellationToken.None);

        Assert.AreEqual(access, tokens.AccessToken);
        Assert.AreEqual("refresh-new", tokens.RefreshToken);
        Assert.AreEqual("acct-new", tokens.AccountId);
        StringAssert.Contains(handler.LastBody!, "refresh_token");
    }

    [TestMethod]
    public async Task RefreshWithoutStoredTokenIsActionable()
    {
        OpenAiCodexOAuthFlow flow = new(new HttpClient(new FakeHandler()));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await flow.RefreshAsync(
                new OAuthTokenSet("bearer", RefreshToken: null, ExpiresAt: null),
                CancellationToken.None));

        StringAssert.Contains(exception.Message, "winharness login --provider openai");
    }

    [TestMethod]
    public void DefaultModelsAreSeeded()
    {
        Assert.IsTrue(OpenAiCodexOAuthFlow.DefaultModels.Count >= 2);
        Assert.IsTrue(OpenAiCodexOAuthFlow.DefaultModels.Any(model => model.Id.Contains("gpt", StringComparison.OrdinalIgnoreCase)));
    }

    private static string CreateJwtWithAccount(string accountId)
    {
        // Minimal unsigned JWT: header.payload.sig with chatgpt_account_id claim.
        string header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        string payloadJson = "{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + accountId + "\"}}";
        string body = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        return $"{header}.{body}.sig";
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
