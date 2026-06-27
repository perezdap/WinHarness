using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Chat;

internal sealed class ChatSession
{
    public ChatSession(string providerId, string modelId, bool renderMarkdown)
    {
        ProviderId = providerId;
        ModelId = modelId;
        RenderMarkdown = renderMarkdown;
        Skills = SkillRegistry.Discover(Environment.CurrentDirectory);
    }

    public string ProviderId { get; set; }

    public string ModelId { get; set; }

    public bool RenderMarkdown { get; set; }

    public IReadOnlyList<SkillDefinition> Skills { get; }

    public SkillDefinition? SelectedSkill { get; set; }

    public ConversationState Conversation { get; } = new();

    public ConversationState CreateRunConversation(string prompt)
    {
        ConversationState conversation = new();
        if (SelectedSkill is not null)
        {
            conversation.Add(new ConversationMessage(ConversationRole.System, SelectedSkill.SystemPrompt));
        }

        foreach (ConversationMessage message in Conversation.Messages)
        {
            conversation.Add(message);
        }

        conversation.Add(new ConversationMessage(ConversationRole.User, prompt));
        return conversation;
    }
}
