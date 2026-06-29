using WinHarness.Configuration;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Handlers for session tree navigation, fork, and compaction slash commands.
/// </summary>
internal static class SlashCommandAdvanced
{
    public static ValueTask<SlashCommandResult> TreeAsync(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.SessionManager.IsPersisted)
        {
            return new(SlashCommandResult.Handled(
            [
                "Branching requires a persisted session. Use --continue or omit --no-session.",
            ]));
        }

        Action<string> onBranch = entryId =>
        {
            session.SessionManager.BranchTo(entryId);
            session.SyncConversationFromSession();
        };

        IReadOnlyList<string> messages = SessionTreePicker.PickAndBranch(session.SessionManager, onBranch);

        return new(SlashCommandResult.Handled(messages));
    }

    public static async ValueTask<SlashCommandResult> ForkAsync(
        ChatSession session,
        SlashCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        if (!session.SessionManager.IsPersisted)
        {
            return SlashCommandResult.Handled(["/fork requires a persisted session."]);
        }

        SessionForkService forkService = new(context.SessionFactory);
        ForkResult result = await forkService.ForkAsync(
            session.SessionManager,
            session.WorkspaceRoot,
            context.CancellationToken).ConfigureAwait(false);

        session.ReplaceSessionManager(result.SessionManager);
        session.SyncConversationFromSession();

        return SlashCommandResult.Handled([$"Forked to new session: {result.FilePath}"]);
    }

    public static async ValueTask<SlashCommandResult> CompactAsync(
        ChatSession session,
        string instructions,
        SlashCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        if (!session.SessionManager.IsPersisted)
        {
            return SlashCommandResult.Handled(["/compact requires a persisted session."]);
        }

        SessionCompactionService compactionService = new(context.AgentRuntime);
        CompactionResult result = await compactionService.CompactAsync(
            session.SessionManager,
            session.ProviderId,
            session.ModelId,
            instructions.Length > 0 ? instructions : null,
            session.SelectedSkill?.SystemPrompt,
            session.WorkspaceRoot,
            context.CancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return SlashCommandResult.Handled([result.Message!]);
        }

        session.SyncConversationFromSession();
        return SlashCommandResult.Handled([result.Message!]);
    }
}