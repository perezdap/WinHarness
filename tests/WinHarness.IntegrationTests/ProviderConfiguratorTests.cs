using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Infrastructure.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;

namespace WinHarness.IntegrationTests;

[TestClass]
public sealed class ProviderConfiguratorTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "WinHarnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task AddProviderStoresApiKeyAndBecomesDefault()
    {
        ConfigStore store = new(_directory);
        InMemoryCredentialStore credentials = new();
        ProviderConfigurator configurator = new(store, credentials);

        ProviderOptions provider = await configurator.AddProviderAsync(
            "openai-main",
            "https://api.openai.com/v1",
            "sk-test",
            makeDefault: false,
            CancellationToken.None);

        Assert.AreEqual("WinHarness:openai-main", provider.CredentialName);
        Assert.AreEqual("sk-test", credentials.Secrets["WinHarness:openai-main"]);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("openai-main", saved.DefaultProvider);
        Assert.AreEqual(1, saved.Providers.Count);
    }

    [TestMethod]
    public async Task AddModelPromotesDefaultModelForDefaultProvider()
    {
        ConfigStore store = new(_directory);
        InMemoryCredentialStore credentials = new();
        ProviderConfigurator configurator = new(store, credentials);

        await configurator.AddProviderAsync("local", "http://localhost:11434/v1", apiKey: null, makeDefault: true, CancellationToken.None);
        ModelOptions model = await configurator.AddModelAsync(
            "local",
            "coder",
            "qwen2.5-coder:latest",
            new ProviderCapabilities(true, true, false, false, false, false),
            makeDefault: false,
            CancellationToken.None);

        Assert.AreEqual("coder", model.Id);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("coder", saved.DefaultModel);
    }

    [TestMethod]
    public async Task RemoveProviderDeletesCredentialAndClearsDefault()
    {
        ConfigStore store = new(_directory);
        InMemoryCredentialStore credentials = new();
        ProviderConfigurator configurator = new(store, credentials);

        await configurator.AddProviderAsync("openai-main", "https://api.openai.com/v1", "sk-test", makeDefault: true, CancellationToken.None);
        await configurator.RemoveProviderAsync("openai-main", CancellationToken.None);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(0, saved.Providers.Count);
        Assert.AreEqual(string.Empty, saved.DefaultProvider);
        Assert.IsFalse(credentials.Secrets.ContainsKey("WinHarness:openai-main"));
    }

    [TestMethod]
    public async Task AddProviderRejectsRelativeBaseUrl()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await configurator.AddProviderAsync("bad", "not-a-url", apiKey: null, makeDefault: false, CancellationToken.None));
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public ValueTask<string?> GetSecretAsync(string targetName, CancellationToken cancellationToken)
            => ValueTask.FromResult(Secrets.TryGetValue(targetName, out string? value) ? value : null);

        public ValueTask SetSecretAsync(string targetName, string secret, CancellationToken cancellationToken)
        {
            Secrets[targetName] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSecretAsync(string targetName, CancellationToken cancellationToken)
        {
            Secrets.Remove(targetName);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> ListTargetNamesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([.. Secrets.Keys]);
    }
}
