using Spectre.Console;
using WinHarness.Context;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Resolves <see cref="ISessionManager"/> from CLI session flags.
/// </summary>
internal static class ChatSessionBootstrap
{
    /// <summary>
    /// Resolves the session manager for a chat invocation.
    /// </summary>
    public static async ValueTask<ISessionManager> ResolveAsync(
        SessionManagerFactory factory,
        ChatSessionBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        string cwd = Environment.CurrentDirectory;

        if (request.NoSession || ShouldUseEphemeralOneShot(request))
        {
            return factory.InMemory(cwd);
        }

        if (request.Resume)
        {
            return await PickSessionOrCreateAsync(factory, cwd, request.Name, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.Session))
        {
            return await OpenByPathOrIdAsync(factory, cwd, request.Session, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<SessionSummary> existing = await factory.ListAsync(cwd, cancellationToken)
            .ConfigureAwait(false);
        bool createdNew = existing.Count == 0;

        ISessionManager sessionManager = await factory.ContinueRecentAsync(cwd, cancellationToken)
            .ConfigureAwait(false);

        if (createdNew && !string.IsNullOrWhiteSpace(request.Name))
        {
            await sessionManager.AppendSessionInfoAsync(request.Name, cancellationToken)
                .ConfigureAwait(false);
        }

        return sessionManager;
    }

    /// <summary>
    /// Creates a <see cref="ChatSession"/> from a resolved session manager.
    /// </summary>
    public static ChatSession CreateChatSession(
        ISessionManager sessionManager,
        IContextFileLoader contextFileLoader,
        string providerId,
        string modelId,
        bool renderMarkdown,
        bool trustProjectLocal = true)
    {
        string workspaceRoot = Environment.CurrentDirectory;
        (string? restoredProvider, string? restoredModel) = TryRestoreModelChange(sessionManager);

        ChatSession session = new(
            sessionManager,
            contextFileLoader,
            workspaceRoot,
            restoredProvider ?? providerId,
            restoredModel ?? modelId,
            renderMarkdown,
            trustProjectLocal);
        session.SyncConversationFromSession();
        return session;
    }

    private static bool ShouldUseEphemeralOneShot(ChatSessionBootstrapRequest request)
    {
        if (!request.IsOneShot)
        {
            return false;
        }

        return !request.ContinueSession
            && string.IsNullOrWhiteSpace(request.Session)
            && !request.Resume;
    }

    /// <summary>
    /// Shows the session picker and opens the selected session.
    /// </summary>
    public static async ValueTask<ISessionManager?> PickSessionAsync(
        SessionManagerFactory factory,
        string cwd,
        string? nameForNewSession,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionSummary> summaries = await factory.ListAsync(cwd, cancellationToken)
            .ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            return null;
        }

        if (summaries.Count == 1)
        {
            return await factory.OpenAsync(summaries[0].FilePath, cancellationToken).ConfigureAwait(false);
        }

        if (Console.IsInputRedirected)
        {
            return await factory.OpenAsync(summaries[0].FilePath, cancellationToken).ConfigureAwait(false);
        }

        Table table = new Table()
            .Border(TableBorder.Square)
            .AddColumn("Session")
            .AddColumn("Name")
            .AddColumn("Messages")
            .AddColumn("Modified");

        foreach (SessionSummary summary in summaries)
        {
            table.AddRow(
                summary.SessionId,
                summary.DisplayName ?? summary.FirstUserPreview ?? "(untitled)",
                summary.MessageCount.ToString(),
                summary.LastModified.LocalDateTime.ToString("g"));
        }

        AnsiConsole.Write(table);

        SessionSummary? selected = await InteractivePicker.ShowAsync(
            new SelectionPrompt<SessionSummary>()
                .Title("Select a session to resume (Esc to cancel)")
                .PageSize(10)
                .AddChoices(summaries)
                .UseConverter(static summary =>
                    $"{summary.SessionId} · {summary.DisplayName ?? summary.FirstUserPreview ?? "(untitled)"}"),
            "Session selection cancelled.").ConfigureAwait(false);

        if (selected is null)
        {
            return null;
        }

        return await factory.OpenAsync(selected.FilePath, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ISessionManager> PickSessionOrCreateAsync(
        SessionManagerFactory factory,
        string cwd,
        string? nameForNewSession,
        CancellationToken cancellationToken)
    {
        ISessionManager? picked = await PickSessionAsync(factory, cwd, nameForNewSession, cancellationToken)
            .ConfigureAwait(false);
        if (picked is not null)
        {
            return picked;
        }

        AnsiConsole.MarkupLine("[dim]No saved sessions for this workspace. Starting a new session.[/]");
        ISessionManager created = await factory.CreateAsync(cwd, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(nameForNewSession))
        {
            await created.AppendSessionInfoAsync(nameForNewSession, cancellationToken)
                .ConfigureAwait(false);
        }

        return created;
    }

    private static async ValueTask<ISessionManager> OpenByPathOrIdAsync(
        SessionManagerFactory factory,
        string cwd,
        string session,
        CancellationToken cancellationToken)
    {
        string trimmed = session.Trim();
        if (File.Exists(trimmed))
        {
            return await factory.OpenAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SessionSummary> summaries = await factory.ListAsync(cwd, cancellationToken)
            .ConfigureAwait(false);
        List<SessionSummary> matches = summaries
            .Where(candidate =>
                string.Equals(candidate.SessionId, trimmed, StringComparison.OrdinalIgnoreCase)
                || candidate.SessionId.EndsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                || candidate.FilePath.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"No session matches '{trimmed}'.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Session id '{trimmed}' is ambiguous ({matches.Count} matches). Use the full file path.");
        }

        return await factory.OpenAsync(matches[0].FilePath, cancellationToken).ConfigureAwait(false);
    }

    internal static (string? ProviderId, string? ModelId) TryRestoreModelChange(ISessionManager sessionManager)
    {
        ModelChangeSessionEntry? modelChange = ActiveBranch.Load(sessionManager).LastOfType<ModelChangeSessionEntry>();
        return modelChange is null ? (null, null) : (modelChange.ProviderId, modelChange.ModelId);
    }
}

/// <summary>
/// CLI session flags for chat bootstrap.
/// </summary>
internal sealed record ChatSessionBootstrapRequest(
    bool IsOneShot,
    bool NoSession,
    bool ContinueSession,
    bool Resume,
    string? Session,
    string? Name,
    bool? ApproveOverride = null);