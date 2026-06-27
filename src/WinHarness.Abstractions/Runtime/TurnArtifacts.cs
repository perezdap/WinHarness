using WinHarness.Conversation;

namespace WinHarness.Runtime;

/// <summary>
/// All messages produced during one agent turn.
/// </summary>
public sealed record TurnArtifacts(IReadOnlyList<ConversationMessage> Messages);