using WinHarness.Context;
using WinHarness.Conversation;
using WinHarness.Runtime;
using WinHarness.Sessions;
using WinHarness.Tools;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Chat;

internal sealed class ChatSession
{
    public ChatSession(string providerId, string modelId, bool renderMarkdown)
        : this(
            Infrastructure.Sessions.SessionManager.InMemory(Environment.CurrentDirectory),
            new EmptyContextFileLoader(),
            Environment.CurrentDirectory,
            providerId,
            modelId,
            renderMarkdown)
    {
    }

    public ChatSession(ISessionManager sessionManager, string providerId, string modelId, bool renderMarkdown)
        : this(sessionManager, new EmptyContextFileLoader(), Environment.CurrentDirectory, providerId, modelId, renderMarkdown)
    {
    }

    public ChatSession(
        ISessionManager sessionManager,
        IContextFileLoader? contextFileLoader,
        string workspaceRoot,
        string providerId,
        string modelId,
        bool renderMarkdown,
        bool trustProjectLocal = true)
    {
        SessionManager = sessionManager;
        ContextFileLoader = contextFileLoader ?? new EmptyContextFileLoader();
        WorkspaceRoot = workspaceRoot;
        TrustProjectLocal = trustProjectLocal;
        ProjectContext = ContextFileLoader.Load(workspaceRoot, trustProjectLocal);
        ProviderId = providerId;
        ModelId = modelId;
        RenderMarkdown = renderMarkdown;
        Skills = SkillRegistry.Discover(workspaceRoot, trustProjectLocal);
        Templates = PromptTemplateRegistry.Discover(workspaceRoot, trustProjectLocal);
    }

    public ISessionManager SessionManager { get; private set; }

    public IContextFileLoader ContextFileLoader { get; }

    public string WorkspaceRoot { get; }

    public bool TrustProjectLocal { get; }

    public ProjectContext ProjectContext { get; private set; }

    public string ProviderId { get; set; }

    public string ModelId { get; set; }

    public bool RenderMarkdown { get; set; }

    public string? ReasoningEffort { get; set; }

    public ToolFilter? ToolFilter { get; set; }

    public SteeringQueue Steering { get; } = new();

    public IReadOnlyList<SkillDefinition> Skills { get; }

    public IReadOnlyList<PromptTemplate> Templates { get; }

    public SkillDefinition? SelectedSkill { get; set; }

    public ConversationState Conversation { get; } = new();

    public bool IsEphemeral => !SessionManager.IsPersisted;

    public int CountActiveBranchMessages() =>
        ActiveBranch.Load(SessionManager).CountMessageEntries();

    public void ReplaceSessionManager(ISessionManager sessionManager)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        SessionManager = sessionManager;
        SyncConversationFromSession();
    }

    public void SyncConversationFromSession()
    {
        ConversationState built = SessionManager.BuildConversation(skillSystemPrompt: null);
        Conversation.Clear();
        foreach (ConversationMessage message in built.Messages)
        {
            Conversation.Add(message);
        }
    }

    public ConversationState BuildRunConversation(string prompt)
    {
        ConversationState conversation = SessionManager.BuildConversation(SelectedSkill?.SystemPrompt);
        conversation.Add(ConversationMessage.FromText(ConversationRole.User, prompt));
        return conversation;
    }

    public ConversationState CreateRunConversation(string prompt) => BuildRunConversation(prompt);

    public async ValueTask AppendTurnAsync(TurnArtifacts? turnArtifacts, CancellationToken cancellationToken)
    {
        if (turnArtifacts is null || turnArtifacts.Messages.Count == 0)
        {
            return;
        }

        await SessionManager.AppendMessagesAsync(turnArtifacts.Messages, cancellationToken).ConfigureAwait(false);
        SyncConversationFromSession();
    }

    public void ReloadProjectContext()
    {
        ProjectContext = ContextFileLoader.Load(WorkspaceRoot, TrustProjectLocal);
    }
}

internal sealed class EmptyContextFileLoader : IContextFileLoader
{
    public ProjectContext Load(string workspaceRoot)
    {
        _ = workspaceRoot;
        return new ProjectContext(null, null, string.Empty);
    }
}