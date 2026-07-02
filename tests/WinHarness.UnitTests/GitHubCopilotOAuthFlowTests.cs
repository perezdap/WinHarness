using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class GitHubCopilotOAuthFlowTests
{
    [TestMethod]
    public void ExtractsBaseUrlFromProxyEndpoint()
    {
        string token = "tid=abc;exp=123;proxy-ep=proxy.individual.githubcopilot.com;rt=1";

        Assert.AreEqual("https://api.individual.githubcopilot.com", GitHubCopilotOAuthFlow.ExtractBaseUrl(token));
    }

    [TestMethod]
    public void FallsBackToDefaultBaseUrlWithoutProxyEndpoint()
    {
        Assert.AreEqual(GitHubCopilotOAuthFlow.DefaultBaseUrl, GitHubCopilotOAuthFlow.ExtractBaseUrl("tid=abc;exp=123"));
    }

    [TestMethod]
    public async Task StartDeviceFlowParsesResponse()
    {
        FakeHandler handler = new(
            """{"device_code":"dc1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","interval":5,"expires_in":900}""");
        GitHubCopilotOAuthFlow flow = new(new HttpClient(handler));

        CopilotDeviceCode device = await flow.StartDeviceFlowAsync(CancellationToken.None);

        Assert.AreEqual("dc1", device.DeviceCode);
        Assert.AreEqual("ABCD-1234", device.UserCode);
        Assert.AreEqual(900, device.ExpiresIn);
        StringAssert.Contains(handler.LastBody!, "scope=read%3Auser");
    }

    [TestMethod]
    public async Task StartDeviceFlowRejectsNonHttpVerificationUri()
    {
        FakeHandler handler = new(
            """{"device_code":"dc1","user_code":"ABCD","verification_uri":"file:///evil","expires_in":900}""");
        GitHubCopilotOAuthFlow flow = new(new HttpClient(handler));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await flow.StartDeviceFlowAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task PollReturnsTokenAfterPending()
    {
        FakeHandler handler = new(
            """{"error":"authorization_pending"}""",
            """{"access_token":"gho_token"}""");
        GitHubCopilotOAuthFlow flow = new(new HttpClient(handler));
        CopilotDeviceCode device = new("dc1", "ABCD", "https://github.com/login/device", Interval: 0, ExpiresIn: 30);

        string token = await flow.PollForGitHubTokenAsync(device, CancellationToken.None);

        Assert.AreEqual("gho_token", token);
        Assert.AreEqual(2, handler.Requests);
    }

    [TestMethod]
    public async Task PollFailsOnTerminalError()
    {
        FakeHandler handler = new(
            """{"error":"access_denied","error_description":"user denied"}""");
        GitHubCopilotOAuthFlow flow = new(new HttpClient(handler));
        CopilotDeviceCode device = new("dc1", "ABCD", "https://github.com/login/device", Interval: 0, ExpiresIn: 30);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await flow.PollForGitHubTokenAsync(device, CancellationToken.None));
        StringAssert.Contains(exception.Message, "access_denied");
    }

    [TestMethod]
    public async Task ExchangeProducesTokenSetWithProxyBaseUrl()
    {
        long expiresAt = DateTimeOffset.Parse("2026-07-02T13:00:00Z").ToUnixTimeSeconds();
        FakeHandler handler = new(
            $$"""{"token":"tid=1;proxy-ep=proxy.individual.githubcopilot.com;x=y","expires_at":{{expiresAt}}}""");
        GitHubCopilotOAuthFlow flow = new(new HttpClient(handler));

        OAuthTokenSet tokens = await flow.ExchangeForBearerAsync("gho_token", CancellationToken.None);

        Assert.AreEqual("gho_token", tokens.RefreshToken);
        Assert.AreEqual("https://api.individual.githubcopilot.com", tokens.BaseUrl);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(expiresAt), tokens.ExpiresAt);
        StringAssert.Contains(handler.LastAuthorization!, "Bearer gho_token");
    }

    [TestMethod]
    public async Task RefreshWithoutStoredGitHubTokenIsActionable()
    {
        GitHubCopilotOAuthFlow flow = new(new HttpClient(new FakeHandler()));

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await flow.RefreshAsync(
                new OAuthTokenSet("bearer", RefreshToken: null, ExpiresAt: null),
                CancellationToken.None));
        StringAssert.Contains(exception.Message, "winharness login");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public FakeHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public int Requests { get; private set; }

        public string? LastBody { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var values)
                ? string.Join(" ", values)
                : null;
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
