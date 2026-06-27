namespace WinHarness.Conversation;

/// <summary>
/// Provider-neutral conversation state.
/// </summary>
public sealed class Conversation
{
    private readonly List<ConversationMessage> _messages = [];

    /// <summary>
    /// Gets conversation messages in order.
    /// </summary>
    public IReadOnlyList<ConversationMessage> Messages => _messages;

    /// <summary>
    /// Adds a message.
    /// </summary>
    public void Add(ConversationMessage message)
    {
        _messages.Add(message);
    }

    /// <summary>
    /// Removes all messages, resetting the conversation.
    /// </summary>
    public void Clear()
    {
        _messages.Clear();
    }
}

/// <summary>
/// A conversation message.
/// </summary>
public sealed record ConversationMessage(
    ConversationRole Role,
    IReadOnlyList<ContentBlock> Content,
    string? ProviderId = null,
    string? ModelId = null,
    MessageUsage? Usage = null)
{
    /// <summary>
    /// Creates a text-only message.
    /// </summary>
    public static ConversationMessage FromText(ConversationRole role, string text) =>
        new(role, [ContentBlock.CreateText(text)]);

    /// <summary>
    /// Gets concatenated text from all text blocks.
    /// </summary>
    public string Text
    {
        get
        {
            if (Content.Count == 0)
            {
                return string.Empty;
            }

            return string.Concat(
                Content
                    .Where(static block => block.Kind == ContentBlockKind.Text && block.Text is not null)
                    .Select(static block => block.Text));
        }
    }
}

/// <summary>
/// Conversation roles.
/// </summary>
public enum ConversationRole
{
    /// <summary>
    /// System instruction.
    /// </summary>
    System,

    /// <summary>
    /// User input.
    /// </summary>
    User,

    /// <summary>
    /// Assistant output.
    /// </summary>
    Assistant,

    /// <summary>
    /// Tool result.
    /// </summary>
    Tool
}