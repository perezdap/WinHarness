namespace WinHarness.Providers;

/// <summary>
/// Infers <see cref="ProviderCapabilities"/> from a discovered
/// <see cref="CatalogModel"/>, applying Tier 1 (endpoint-advertised value),
/// Tier 3 (model-id name heuristic), and Tier 4 (conservative default) in that
/// precedence order. The non-chat id guard (<c>embedding</c>/<c>moderation</c>/
/// <c>whisper</c>/<c>tts</c>/<c>dall-e</c>/<c>rerank</c>) overrides everything
/// to all-false. This is the no-network fallback used by
/// <see cref="IModelCapabilityResolver"/> when both the endpoint and the
/// OpenRouter cross-reference are silent.
/// </summary>
public interface IModelCapabilityInferrer
{
    /// <summary>
    /// Infers capabilities for a single discovered model.
    /// </summary>
    ProviderCapabilities Infer(CatalogModel model);
}

/// <summary>
/// Default implementation. Pure, synchronous, no network.
/// </summary>
public sealed class ModelCapabilityInferrer : IModelCapabilityInferrer
{
    /// <inheritdoc />
    public ProviderCapabilities Infer(CatalogModel model)
    {
        string id = model.Id ?? string.Empty;
        string lower = id.ToLowerInvariant();
        bool isNonChat = IsNonChatId(lower);

        // Tier 1 wins when non-null; Tier 3 name heuristic fills in when silent;
        // Tier 4 conservative default when neither applies.
        bool streaming = !isNonChat;
        bool toolCalling = model.ToolCalling ?? !isNonChat;
        bool vision = model.Vision ?? false;
        bool reasoning = model.Reasoning ?? (model.SupportedReasoningEfforts is { Count: > 0 });
        bool promptCaching = model.PromptCaching ?? InferPromptCachingByName(lower);
        bool structuredOutput = model.StructuredOutput ?? InferStructuredOutputByName(lower);

        if (isNonChat)
        {
            streaming = false;
            toolCalling = false;
            vision = false;
            promptCaching = false;
            structuredOutput = false;
            reasoning = false;
        }

        return new ProviderCapabilities(
            Streaming: streaming,
            ToolCalling: toolCalling,
            Vision: vision,
            PromptCaching: promptCaching,
            StructuredOutput: structuredOutput,
            Reasoning: reasoning);
    }

    /// <summary>
    /// Returns true when the (lowercased) model id names a non-chat model
    /// (embeddings, moderation, speech, image generation, reranking). Shared
    /// with <see cref="ModelCapabilityResolver"/> so the non-chat guard wins
    /// over OpenRouter cross-reference overrides.
    /// </summary>
    public static bool IsNonChatId(string lowercasedId)
    {
        return lowercasedId.Contains("embedding", StringComparison.Ordinal) ||
               lowercasedId.Contains("moderation", StringComparison.Ordinal) ||
               lowercasedId.Contains("whisper", StringComparison.Ordinal) ||
               lowercasedId.Contains("tts", StringComparison.Ordinal) ||
               lowercasedId.Contains("dall-e", StringComparison.Ordinal) ||
               lowercasedId.Contains("rerank", StringComparison.Ordinal);
    }

    private static bool InferPromptCachingByName(string lower)
    {
        return lower.Contains("claude-3", StringComparison.Ordinal) ||
               lower.Contains("gemini-1.5", StringComparison.Ordinal) ||
               lower.Contains("gemini-2.0", StringComparison.Ordinal) ||
               lower.Contains("deepseek", StringComparison.Ordinal);
    }

    private static bool InferStructuredOutputByName(string lower)
    {
        return lower.Contains("gpt-4o", StringComparison.Ordinal) ||
               lower.Contains("gpt-4-turbo", StringComparison.Ordinal) ||
               lower.Contains("gemini-1.5", StringComparison.Ordinal) ||
               lower.Contains("gemini-2.0", StringComparison.Ordinal);
    }
}
