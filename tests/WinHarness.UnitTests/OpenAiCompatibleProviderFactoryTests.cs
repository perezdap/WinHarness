using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;
using WinHarness.Serialization;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class OpenAiCompatibleProviderFactoryTests
{
    [TestMethod]
    public void CreatesConfiguredProviderWithModelCapabilities()
    {
        WinHarnessOptions options = CreateOptions();
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());

        IChatProvider provider = factory.Create("local", "coder");

        Assert.AreEqual("local", provider.ProviderId);
        Assert.AreEqual("coder", provider.ModelId);
        Assert.IsTrue(provider.Capabilities.Streaming);
        Assert.IsFalse(provider.Capabilities.Vision);
    }

    [TestMethod]
    public void RejectsMissingModel()
    {
        WinHarnessOptions options = CreateOptions();
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore());

        InvalidOperationException? exception = null;
        try
        {
            factory.Create("local", "missing");
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        StringAssert.Contains(exception!.Message, "missing");
    }

    [TestMethod]
    public async Task CreateTokenSourceResolvesApiKeyScheme()
    {
        // Provider "local" has CredentialName = "WinHarness:local" (see
        // CreateOptions). The factory must wire an ApiKeyTokenSource that reads
        // that credential name; the stub store returns "test-key" for any name,
        // so observing "test-key" (not the keyless "not-required" placeholder)
        // proves the credential name was forwarded.
        WinHarnessOptions options = CreateOptions();
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore(), []);

        IAuthTokenSource source = factory.CreateTokenSource("local");

        Assert.AreEqual("test-key", await source.GetAccessTokenAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CreateTokenSourceResolvesOAuthSchemeAndReturnsFreshBearer()
    {
        WinHarnessOptions options = CreateOptions();
        options.Providers.Add(new ProviderOptions
        {
            Id = "copilot",
            Kind = "openai-compatible",
            BaseUrl = "https://api.individual.githubcopilot.com",
            Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "copilot" }
        });

        InMemoryCredentialStore store = new();
        // OAuthTokenSource uses TimeProvider.System by default, so the stored
        // bearer must be in the future relative to the real clock to avoid an
        // unwanted refresh.
        OAuthTokenSet fresh = new("bearer-fresh", "gho-refresh", DateTimeOffset.UtcNow.AddHours(1));
        await store.SetSecretAsync(
            OAuthCredentialNames.ForProvider("copilot"),
            JsonSerializer.Serialize(fresh, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
            CancellationToken.None);

        StubRefresher refresher = new("copilot");
        OpenAiCompatibleProviderFactory factory = new(options, store, [refresher]);

        IAuthTokenSource source = factory.CreateTokenSource("copilot");

        Assert.AreEqual("bearer-fresh", await source.GetAccessTokenAsync(CancellationToken.None));
        Assert.AreEqual(0, refresher.Refreshes, "Fresh bearer must not trigger a refresh.");
    }

    [TestMethod]
    public async Task CreateTokenSourceResolvesLegacyOpenAiOAuthAlias()
    {
        WinHarnessOptions options = CreateOptions();
        options.Providers.Add(new ProviderOptions
        {
            Id = "openai",
            Kind = "openai-codex-responses",
            BaseUrl = OpenAiCodexOAuthFlow.DefaultBaseUrl,
            Auth = new ProviderAuthOptions { Scheme = "oauth", OAuthProvider = "openai" }
        });

        InMemoryCredentialStore store = new();
        OAuthTokenSet fresh = new("bearer-fresh", "refresh", DateTimeOffset.UtcNow.AddHours(1));
        await store.SetSecretAsync(
            OAuthCredentialNames.ForProvider("openai"),
            JsonSerializer.Serialize(fresh, WinHarnessJsonSerializerContext.Default.OAuthTokenSet),
            CancellationToken.None);

        StubRefresher refresher = new(OpenAiCodexOAuthFlow.ProviderId);
        OpenAiCompatibleProviderFactory factory = new(options, store, [refresher]);

        IAuthTokenSource source = factory.CreateTokenSource("openai");

        Assert.AreEqual("bearer-fresh", await source.GetAccessTokenAsync(CancellationToken.None));
        Assert.AreEqual(0, refresher.Refreshes, "Fresh bearer must not trigger a refresh.");
    }

    [TestMethod]
    public void CreateTokenSourceThrowsActionableErrorForUnknownProvider()
    {
        WinHarnessOptions options = CreateOptions();
        OpenAiCompatibleProviderFactory factory = new(options, new FakeCredentialStore(), []);

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => factory.CreateTokenSource("nope"));

        StringAssert.Contains(exception.Message, "nope");
    }

    private static WinHarnessOptions CreateOptions()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "coder"
        };

        ProviderOptions provider = new()
        {
            Id = "local",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1",
            CredentialName = "WinHarness:local"
        };

        provider.Models.Add(new ModelOptions
        {
            Id = "coder",
            ProviderModelId = "qwen2.5-coder",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: false,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: false,
                Reasoning: false)
        });

        options.Providers.Add(provider);
        return options;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>("test-key");
        }

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(["test-key"]);
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
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

    private sealed class StubRefresher : IOAuthTokenRefresher
    {
        public StubRefresher(string providerId) => OAuthProviderId = providerId;

        public string OAuthProviderId { get; }

        public int Refreshes { get; private set; }

        public ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken)
        {
            Refreshes++;
            return ValueTask.FromResult(current with { AccessToken = "refreshed-bearer" });
        }
    }
}
