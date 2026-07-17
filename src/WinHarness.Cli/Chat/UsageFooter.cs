using System.Globalization;
using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Formats the per-turn status footer: model, estimated context usage, turn
/// token usage, and session token totals, computed from the active branch.
/// </summary>
internal static class UsageFooter
{
    public static string Format(ChatSession session, WinHarnessOptions options, MessageUsage? turnUsage)
    {
        long estimated = AutoCompactionService.EstimateConversationTokens(session);
        int contextWindow = AutoCompactionService.ResolveContextWindow(options, session.ProviderId, session.ModelId);
        double percent = contextWindow > 0 ? Math.Min(100.0, estimated * 100.0 / contextWindow) : 0;

        (long input, long output) = SumSessionUsage(session);

        string effort = StatusLineFormatter.ResolveEffort(session, options);

        List<string> parts =
        [
            $"{session.ModelId} @ {session.ProviderId}",
            $"effort {effort}",
            $"ctx ~{percent.ToString("F0", CultureInfo.InvariantCulture)}% ({Compact(estimated)}/{Compact(contextWindow)})"
        ];

        if (turnUsage is not null)
        {
            parts.Add($"turn ↑{Compact(turnUsage.InputTokens ?? 0)} ↓{Compact(turnUsage.OutputTokens ?? 0)}");
        }

        if (input > 0 || output > 0)
        {
            parts.Add($"session ↑{Compact(input)} ↓{Compact(output)}");
        }

        return "[" + string.Join(" | ", parts) + "]";
    }

    /// <summary>
    /// Sums assistant-message usage over the active branch.
    /// </summary>
    public static (long InputTokens, long OutputTokens) SumSessionUsage(ChatSession session) =>
        ActiveBranch.Load(session.SessionManager).SumAssistantUsage();

    /// <summary>
    /// Finds the most recent assistant usage on the active branch.
    /// </summary>
    public static MessageUsage? FindLastTurnUsage(ChatSession session) =>
        ActiveBranch.Load(session.SessionManager)
            .LastOfType<MessageSessionEntry>(static entry =>
                entry.Message is { Role: ConversationRole.Assistant, Usage: not null })
            ?.Message.Usage;

    /// <summary>
    /// Renders token counts compactly: 950 → "950", 30_400 → "30.4k", 1_000_000 → "1.0m".
    /// </summary>
    public static string Compact(long tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => (tokens / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "m",
            >= 1_000 => (tokens / 1_000.0).ToString("F1", CultureInfo.InvariantCulture) + "k",
            _ => tokens.ToString(CultureInfo.InvariantCulture)
        };
    }
}
