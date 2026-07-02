using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Serialization;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class AuthTokenSourceTests
{
    [TestMethod]
    public async Task ApiKeySourceReturnsPlaceholderWhenKeyless()
    {
        ApiKeyTokenSource source = new(new FakeCredentialStore(), credentialName: null);

        Assert.AreEqual("not-required", await source.GetAccessTokenAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ApiKeySourceReadsSecret()
    {
        FakeCredentialStore store = new();
        await store.SetSecretAsync("WinHarness:main", "sk-123", CancellationToken.None);
        ApiKeyTokenSource source = new(store, "WinHarness:main");

        Assert.AreEqual("sk-123", await source.GetAccessTokenAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ApiKeySourceThrowsWhenMissing()
    {
        ApiKeyTokenSource source = new(new FakeCredentialStore(), "WinHarness:missing");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await source.GetAccessTokenAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task OAuthSourceReturnsStoredTokenWhenFresh()
    {
        FakeCredentialStore store = new();
        FakeTimeProvider time = new(DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        await StoreTokensAsync(store, "sub", new OAuthTokenSet(
            "fresh-token", "refresh", time.GetUtcNow().AddHours(1)));
        CountingRefresher refresher = new();
        OAuthTokenSource source = new(store, refresher, "sub", time);

        Assert.AreEqual("fresh-token", await source.GetAccessTokenAsync(CancellationToken.None));
        Assert.AreEqual(0, refresher.Refreshes);
    }

    [TestMethod]
    public async Task OAuthSourceRefreshesNearExpiryAndPersists()
    {
        FakeCredentialStore store = new();
        FakeTimeProvider time = new(DateTimeOffset.Parse("2026-07-01T00:00:00Z"));
        // Expires within the 5-minute skew.
        await StoreTokensAsync(store, "sub", new OAuthTokenSet(
            "stale-token", "refresh", time.GetUtcNow().AddMinutes(2)));
        CountingRefresher refresher = new();
        OAuthTokenSource source = new(store, refresher, "sub", time);

        string token = await source.GetAccessTokenAsync(CancellationToken.None);

        Assert.AreEqual("refreshed-token", token);
        Assert.AreEqual(1, refresher.Refreshes);
        string persisted = (await store.GetSecretAsync(
            OAuthCredentialNames.ForProvider("sub"), CancellationToken.None))!;
        StringAssert.Contains(persisted, "refreshed-token");
    }

    [TestMethod]
    public async Task OAuthSourceThrowsActionableErrorWhenNotLoggedIn()
    {
        OAuthTokenSource source = new(new FakeCredentialStore(), new CountingRefresher(), "sub");

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await source.GetAccessTokenAsync(CancellationToken.None));
        StringAssert.Contains(exception.Message, "winharness login");
    }

    [TestMethod]
    public void TokenSetNeedsRefreshRespectsSkew()
    {
        FakeTimeProvider time = new(DateTimeOffset.Parse("2026-07-01T00:00:00Z"));

        Assert.IsFalse(new OAuthTokenSet("t", null, time.GetUtcNow().AddMinutes(10)).NeedsRefresh(time));
        Assert.IsTrue(new OAuthTokenSet("t", null, time.GetUtcNow().AddMinutes(4)).NeedsRefresh(time));
        Assert.IsTrue(new OAuthTokenSet("t", null, time.GetUtcNow().AddMinutes(-1)).NeedsRefresh(time));
        Assert.IsFalse(new OAuthTokenSet("t", null, ExpiresAt: null).NeedsRefresh(time));
    }

    private static async Task StoreTokensAsync(ICredentialStore store, string providerId, OAuthTokenSet tokens)
    {
        await store.SetSecretAsync(
            OAuthCredentialNames.ForProvider(providerId),
            JsonSerializer.Serialize(tokens, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
            CancellationToken.None);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_secrets.GetValueOrDefault(targetName));

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
        {
            _secrets[targetName] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            _secrets.Remove(targetName);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<string>>([.. _secrets.Keys]);
    }

    private sealed class CountingRefresher : IOAuthTokenRefresher
    {
        public int Refreshes { get; private set; }

        public string OAuthProviderId => "fake";

        public ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken)
        {
            Refreshes++;
            return ValueTask.FromResult(current with
            {
                AccessToken = "refreshed-token",
                ExpiresAt = DateTimeOffset.Parse("2026-07-01T01:00:00Z")
            });
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
