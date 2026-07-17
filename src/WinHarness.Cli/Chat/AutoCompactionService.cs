using WinHarness.Configuration;
using WinHarness.Runtime;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Trigger logic for automatic compaction: proactive (estimated tokens near the
/// model's context window before a turn) and reactive (provider rejected the
/// request with a context-overflow error). Reuses <see cref="SessionCompactionService"/>.
/// </summary>
internal static class AutoCompactionService
{
    /// <summary>
    /// Fallback context window when the model config does not declare one.
    /// Deliberately conservative.
    /// </summary>
    public const int DefaultContextWindow = 8192;

    // Rough chars-per-token heuristic; refined later by real usage data (PR-A4).
    private const int CharsPerToken = 4;

    /// <summary>
    /// Compacts the session when the estimated active-context tokens exceed the
    /// model's context window minus the configured reserve. Returns a user-facing
    /// notice when compaction ran, null otherwise.
    /// </summary>
    public static async ValueTask<string?> TryProactiveCompactAsync(
        WinHarnessOptions options,
        ChatSession session,
        IAgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!ShouldAutoCompact(options, session))
        {
            return null;
        }

        int contextWindow = ResolveContextWindow(options, session.ProviderId, session.ModelId);
        long estimatedTokens = EstimateConversationTokens(session);
        if (estimatedTokens <= contextWindow - options.Compaction.ReserveTokens)
        {
            return null;
        }

        CompactionResult result = await CompactAsync(session, runtime, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? $"[auto-compact] context ~{estimatedTokens:N0} tokens near limit ({contextWindow:N0}); {result.Message}"
            : null;
    }

    /// <summary>
    /// Compacts the session after a provider context-overflow failure so the
    /// caller can retry the turn once. Returns a notice when compaction ran,
    /// null when auto-compaction is unavailable for this session.
    /// </summary>
    public static async ValueTask<string?> TryReactiveCompactAsync(
        WinHarnessOptions options,
        ChatSession session,
        IAgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!ShouldAutoCompact(options, session))
        {
            return null;
        }

        CompactionResult result = await CompactAsync(session, runtime, cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? $"[auto-compact] context overflow reported by provider; {result.Message} Retrying turn."
            : null;
    }

    /// <summary>
    /// Matches provider failure text that indicates the request exceeded the
    /// model's context window. Covers common OpenAI-compatible phrasings.
    /// </summary>
    public static bool IsContextOverflow(string? failureMessage)
    {
        if (string.IsNullOrEmpty(failureMessage))
        {
            return false;
        }

        return failureMessage.Contains("context length", StringComparison.OrdinalIgnoreCase) ||
            failureMessage.Contains("context window", StringComparison.OrdinalIgnoreCase) ||
            failureMessage.Contains("maximum context", StringComparison.OrdinalIgnoreCase) ||
            failureMessage.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            failureMessage.Contains("too many tokens", StringComparison.OrdinalIgnoreCase) ||
            (failureMessage.Contains("reduce", StringComparison.OrdinalIgnoreCase) &&
                failureMessage.Contains("tokens", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the configured context window for the active model, falling back
    /// to <see cref="DefaultContextWindow"/> when unset.
    /// </summary>
    public static int ResolveContextWindow(WinHarnessOptions options, string providerId, string modelId)
    {
        ProviderOptions? provider = options.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        ModelOptions? model = provider?.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.OrdinalIgnoreCase));
        return model?.ContextWindow ?? DefaultContextWindow;
    }

    /// <summary>
    /// Estimates active-branch tokens with a chars/4 heuristic over the
    /// conversation the next turn would send.
    /// </summary>
    public static long EstimateConversationTokens(ChatSession session)
    {
        long chars = ActiveBranch.SumMessageTextChars(
            session.SessionManager.BuildConversation(session.SelectedSkill?.SystemPrompt));
        return chars / CharsPerToken;
    }

    private static bool ShouldAutoCompact(WinHarnessOptions options, ChatSession session) =>
        options.Compaction.AutoCompact && session.SessionManager.IsPersisted;

    private static async ValueTask<CompactionResult> CompactAsync(
        ChatSession session,
        IAgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        SessionCompactionService compactionService = new(runtime);
        CompactionResult result = await compactionService.CompactAsync(
            session.SessionManager,
            session.ProviderId,
            session.ModelId,
            instructions: null,
            session.SelectedSkill?.SystemPrompt,
            session.WorkspaceRoot,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            session.SyncConversationFromSession();
        }

        return result;
    }
}
