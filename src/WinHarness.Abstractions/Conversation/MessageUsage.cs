namespace WinHarness.Conversation;

/// <summary>
/// Token usage reported for an assistant message.
/// </summary>
public sealed record MessageUsage(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? TotalTokens = null);