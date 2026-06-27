using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Cli.Chat;

internal sealed class ChatSession
{
    public ChatSession(string providerId, string modelId, bool renderMarkdown)
    {
        ProviderId = providerId;
        ModelId = modelId;
        RenderMarkdown = renderMarkdown;
    }

    public string ProviderId { get; set; }

    public string ModelId { get; set; }

    public bool RenderMarkdown { get; set; }

    public ConversationState Conversation { get; } = new();
}
