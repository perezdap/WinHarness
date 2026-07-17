using WinHarness.Conversation;
using WinHarness.Infrastructure.Sessions;
using WinHarness.Sessions;

namespace WinHarness.Cli.Chat;

/// <summary>
/// Creates a new persisted session file with messages copied from the active branch.
/// </summary>
internal sealed class SessionForkService
{
    private readonly SessionManagerFactory _factory;

    public SessionForkService(SessionManagerFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask<ForkResult> ForkAsync(
        ISessionManager source,
        string cwd,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(cwd);

        if (!source.IsPersisted)
        {
            throw new InvalidOperationException("/fork requires a persisted session.");
        }

        ISessionManager forked = await _factory.CreateAsync(cwd, cancellationToken).ConfigureAwait(false);

        List<ConversationMessage> messages = ActiveBranch.Load(source).FlattenMessages();

        if (messages.Count > 0)
        {
            await forked.AppendMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        }

        return new ForkResult(forked, forked.SessionFilePath!);
    }
}

internal sealed record ForkResult(ISessionManager SessionManager, string FilePath);