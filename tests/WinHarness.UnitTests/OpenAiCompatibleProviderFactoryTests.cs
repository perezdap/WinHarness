using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Platform;
using WinHarness.Providers;

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
            BaseUrl = "http://localhost:11434/v1"
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
    }
}
