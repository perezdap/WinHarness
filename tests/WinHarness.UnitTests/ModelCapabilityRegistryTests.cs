using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Providers;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class ModelCapabilityRegistryTests
{
    [TestMethod]
    public void ResolvesConfiguredCapabilities()
    {
        WinHarnessOptions options = new();
        ProviderOptions provider = new()
        {
            Id = "openai-main",
            Kind = "openai-compatible"
        };

        provider.Models.Add(new ModelOptions
        {
            Id = "coder",
            ProviderModelId = "gpt-test",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: true,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: true,
                Reasoning: true)
        });
        options.Providers.Add(provider);

        ConfigurationModelCapabilityRegistry registry = new(options);

        ProviderCapabilities capabilities = registry.GetCapabilities("openai-main", "coder");

        Assert.IsTrue(capabilities.Streaming);
        Assert.IsTrue(capabilities.ToolCalling);
        Assert.IsTrue(capabilities.StructuredOutput);
        Assert.IsFalse(capabilities.Vision);
    }
}
