using WinHarness.Configuration;
using WinHarness.Conversation;
using WinHarness.Sessions;

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
        return await CloneCoreAsync(session, context, "/fork").ConfigureAwait(false);
    }

    public static async ValueTask<SlashCommandResult> CloneAsync(
        ChatSession session,
        SlashCommandContext context)
    {
        return await CloneCoreAsync(session, context, "/clone").ConfigureAwait(false);
    }

    private static async ValueTask<SlashCommandResult> CloneCoreAsync(
        ChatSession session,
        SlashCommandContext context,
        string command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        if (!session.SessionManager.IsPersisted)
        {
            return SlashCommandResult.Handled([$"{command} requires a persisted session."]);
        }

        SessionForkService forkService = new(context.SessionFactory);
        ForkResult result = await forkService.ForkAsync(
            session.SessionManager,
            session.WorkspaceRoot,
            context.CancellationToken).ConfigureAwait(false);

        session.ReplaceSessionManager(result.SessionManager);
        session.SyncConversationFromSession();

        return SlashCommandResult.Handled([$"Copied active branch to new session: {result.FilePath}"]);
    }

    public static SlashCommandResult Export(ChatSession session, string argument)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.SessionManager.IsPersisted)
        {
            return SlashCommandResult.Handled(["/export requires a persisted session."]);
        }

        string output = argument.Trim();
        if (output.Length == 0)
        {
            output = $"session-{session.SessionManager.Header.Id}.html";
        }

        string written = SessionExportService.Export(session.SessionManager, output);
        return SlashCommandResult.Handled([$"Exported active branch to {written}"]);
    }

    public static async ValueTask<SlashCommandResult> ImportAsync(
        ChatSession session,
        string argument,
        SlashCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        string path = argument.Trim();
        if (path.Length == 0)
        {
            return SlashCommandResult.Handled(["Usage: /import <file.jsonl>"]);
        }

        IReadOnlyList<SessionEntry> entries = SessionExportService.ValidateImportFile(path);

        ISessionManager imported = await context.SessionFactory
            .CreateAsync(session.WorkspaceRoot, context.CancellationToken).ConfigureAwait(false);
        List<ConversationMessage> messages = ActiveBranch.FromEntries(entries).FlattenMessages();
        if (messages.Count > 0)
        {
            await imported.AppendMessagesAsync(messages, context.CancellationToken).ConfigureAwait(false);
        }

        session.ReplaceSessionManager(imported);
        session.SyncConversationFromSession();
        return SlashCommandResult.Handled(
            [$"Imported {messages.Count} messages into new session: {imported.SessionFilePath}"]);
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