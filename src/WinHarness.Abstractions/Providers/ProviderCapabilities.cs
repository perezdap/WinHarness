namespace WinHarness.Providers;

/// <summary>
/// Describes model-specific provider capabilities.
/// </summary>
public sealed record ProviderCapabilities(
    bool Streaming,
    bool ToolCalling,
    bool Vision,
    bool PromptCaching,
    bool StructuredOutput,
    bool Reasoning)
{
    /// <summary>
    /// Gets an empty capability set.
    /// </summary>
    public static ProviderCapabilities None { get; } = new(
        Streaming: false,
        ToolCalling: false,
        Vision: false,
        PromptCaching: false,
        StructuredOutput: false,
        Reasoning: false);
}
