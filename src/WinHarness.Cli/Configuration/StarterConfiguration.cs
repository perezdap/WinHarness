using WinHarness.Configuration;
using WinHarness.Providers;

namespace WinHarness.Cli.Configuration;

/// <summary>
/// Creates the default starter configuration for first-run users.
/// </summary>
internal static class StarterConfiguration
{
    public static WinHarnessOptions Create()
    {
        WinHarnessOptions options = new()
        {
            DefaultProvider = "local-ollama",
            DefaultModel = "local-coder"
        };

        ProviderOptions provider = new()
        {
            Id = "local-ollama",
            Kind = "openai-compatible",
            BaseUrl = "http://localhost:11434/v1"
        };

        provider.Models.Add(new ModelOptions
        {
            Id = "local-coder",
            ProviderModelId = "qwen2.5-coder:latest",
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
}
