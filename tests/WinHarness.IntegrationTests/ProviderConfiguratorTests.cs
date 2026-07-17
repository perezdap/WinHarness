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
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);

        Assert.AreEqual("coder", model.Id);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("coder", saved.DefaultModel);
    }

    [TestMethod]
    public async Task SetModelCapabilitiesUpdatesExistingModel()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());

        await configurator.AddProviderAsync("local", "http://localhost:11434/v1", apiKey: null, makeDefault: true, CancellationToken.None);
        await configurator.AddModelAsync(
            "local",
            "coder",
            "qwen2.5-coder:latest",
            ProviderCapabilities.None,
            makeDefault: true,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);

        ProviderCapabilities capabilities = new(
            Streaming: true,
            ToolCalling: true,
            Vision: true,
            PromptCaching: false,
            StructuredOutput: true,
            Reasoning: true);
        ModelOptions model = await configurator.SetModelCapabilitiesAsync(
            "local",
            "coder",
            capabilities,
            CancellationToken.None);

        Assert.IsTrue(model.Capabilities.ToolCalling);
        Assert.IsTrue(model.Capabilities.Vision);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.IsTrue(saved.Providers[0].Models[0].Capabilities.StructuredOutput);
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

    [TestMethod]
    public async Task SetDefaultProviderRepairsModelToFirstOfNewProvider()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);

        string resolvedModel = await configurator.SetDefaultProviderAsync("hosted", CancellationToken.None);

        Assert.AreEqual("gpt-primary", resolvedModel);
        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("hosted", saved.DefaultProvider);
        Assert.AreEqual("gpt-primary", saved.DefaultModel);
    }

    [TestMethod]
    public async Task SetDefaultProviderKeepsModelWhenStillValid()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);
        await configurator.AddModelAsync(
            "hosted",
            "coder",
            "qwen2.5-coder:latest",
            ProviderCapabilities.None,
            makeDefault: false,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);

        // "coder" is hosted's second model; a repair would have picked "gpt-primary".
        string resolvedModel = await configurator.SetDefaultProviderAsync("hosted", CancellationToken.None);

        Assert.AreEqual("coder", resolvedModel);
        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("hosted", saved.DefaultProvider);
        Assert.AreEqual("coder", saved.DefaultModel);
    }

    [TestMethod]
    public async Task SetDefaultProviderClearsModelWhenNewProviderHasNoModels()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);
        await configurator.AddProviderAsync("empty", "https://empty.example.com/v1", apiKey: null, makeDefault: false, CancellationToken.None);

        string resolvedModel = await configurator.SetDefaultProviderAsync("empty", CancellationToken.None);

        Assert.AreEqual(string.Empty, resolvedModel);
        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("empty", saved.DefaultProvider);
        Assert.AreEqual(string.Empty, saved.DefaultModel);
    }

    [TestMethod]
    public async Task SetDefaultProviderThrowsForUnknownProvider()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await configurator.SetDefaultProviderAsync("missing", CancellationToken.None));
        Assert.AreEqual("Provider 'missing' is not configured.", exception.Message);
    }

    [TestMethod]
    public async Task SetDefaultModelSwitchesWithinDefaultProvider()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);
        await configurator.AddModelAsync(
            "local",
            "reviewer",
            "qwen2.5-coder:latest",
            ProviderCapabilities.None,
            makeDefault: false,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);

        await configurator.SetDefaultModelAsync("reviewer", CancellationToken.None);

        WinHarnessOptions saved = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual("local", saved.DefaultProvider);
        Assert.AreEqual("reviewer", saved.DefaultModel);
    }

    [TestMethod]
    public async Task SetDefaultModelRejectsModelFromAnotherProvider()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());
        await SeedLocalAndHostedAsync(configurator);

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await configurator.SetDefaultModelAsync("gpt-primary", CancellationToken.None));
        Assert.AreEqual("Model 'gpt-primary' is not configured for provider 'local'.", exception.Message);
    }

    [TestMethod]
    public async Task SetDefaultModelThrowsWithoutDefaultProvider()
    {
        ConfigStore store = new(_directory);
        ProviderConfigurator configurator = new(store, new InMemoryCredentialStore());

        InvalidOperationException exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await configurator.SetDefaultModelAsync("coder", CancellationToken.None));
        Assert.AreEqual("Configure a default provider before selecting a model.", exception.Message);
    }

    [TestMethod]
    public void RepairDefaultModelKeepsModelThatBelongsToProvider()
    {
        WinHarnessOptions options = CreateRepairOptions();

        Assert.AreEqual("gpt-primary", ProviderConfigurator.RepairDefaultModel(options, "hosted", "gpt-primary"));
    }

    [TestMethod]
    public void RepairDefaultModelFallsBackToFirstModel()
    {
        WinHarnessOptions options = CreateRepairOptions();

        Assert.AreEqual("gpt-primary", ProviderConfigurator.RepairDefaultModel(options, "hosted", "coder"));
    }

    [TestMethod]
    public void RepairDefaultModelClearsModelWhenProviderHasNone()
    {
        WinHarnessOptions options = CreateRepairOptions();

        Assert.AreEqual(string.Empty, ProviderConfigurator.RepairDefaultModel(options, "empty", "coder"));
    }

    [TestMethod]
    public void RepairDefaultModelLeavesEmptyModelUnset()
    {
        WinHarnessOptions options = CreateRepairOptions();

        Assert.AreEqual(string.Empty, ProviderConfigurator.RepairDefaultModel(options, "hosted", string.Empty));
    }

    [TestMethod]
    public void RepairDefaultModelThrowsForUnknownProvider()
    {
        WinHarnessOptions options = CreateRepairOptions();

        InvalidOperationException exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ProviderConfigurator.RepairDefaultModel(options, "missing", "coder"));
        Assert.AreEqual("Provider 'missing' is not configured.", exception.Message);
    }

    private static async Task SeedLocalAndHostedAsync(ProviderConfigurator configurator)
    {
        await configurator.AddProviderAsync("local", "http://localhost:11434/v1", apiKey: null, makeDefault: true, CancellationToken.None);
        await configurator.AddModelAsync(
            "local",
            "coder",
            "qwen2.5-coder:latest",
            ProviderCapabilities.None,
            makeDefault: true,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);
        await configurator.AddProviderAsync("hosted", "https://api.openai.com/v1", apiKey: null, makeDefault: false, CancellationToken.None);
        await configurator.AddModelAsync(
            "hosted",
            "gpt-primary",
            "gpt-4.1",
            ProviderCapabilities.None,
            makeDefault: false,
            contextWindow: null,
            supportedReasoningEfforts: null,
            cancellationToken: CancellationToken.None);
    }

    private static WinHarnessOptions CreateRepairOptions()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local",
            DefaultModel = "coder"
        };
        ProviderOptions local = new() { Id = "local", Kind = "openai-compatible", BaseUrl = "http://localhost:11434/v1" };
        local.Models.Add(new ModelOptions { Id = "coder", ProviderModelId = "qwen2.5-coder:latest" });
        ProviderOptions hosted = new() { Id = "hosted", Kind = "openai-compatible", BaseUrl = "https://api.openai.com/v1" };
        hosted.Models.Add(new ModelOptions { Id = "gpt-primary", ProviderModelId = "gpt-4.1" });
        ProviderOptions empty = new() { Id = "empty", Kind = "openai-compatible", BaseUrl = "https://empty.example.com/v1" };
        options.Providers.Add(local);
        options.Providers.Add(hosted);
        options.Providers.Add(empty);
        return options;
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
