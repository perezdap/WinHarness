using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinHarness.Configuration;
using WinHarness.Providers;
using WinHarness.Serialization;

namespace WinHarness.UnitTests;

[TestClass]
public sealed class WinHarnessJsonSerializerContextTests
{
    [TestMethod]
    public void SerializesConfigurationWithSourceGeneratedContext()
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
            ProviderModelId = "qwen",
            Capabilities = new ProviderCapabilities(
                Streaming: true,
                ToolCalling: false,
                Vision: false,
                PromptCaching: false,
                StructuredOutput: false,
                Reasoning: false)
        });
        options.Providers.Add(provider);

        string json = JsonSerializer.Serialize(options, WinHarnessJsonSerializerContext.Default.WinHarnessOptions);

        StringAssert.Contains(json, "\"defaultProvider\":\"local\"");
        StringAssert.Contains(json, "\"providerModelId\":\"qwen\"");
    }
}
