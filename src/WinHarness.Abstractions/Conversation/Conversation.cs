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
public sealed record ConversationMessage(ConversationRole Role, string Content);

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
