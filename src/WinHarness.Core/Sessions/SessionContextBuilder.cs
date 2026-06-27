using WinHarness.Conversation;
using ConversationState = WinHarness.Conversation.Conversation;

namespace WinHarness.Sessions;

/// <summary>
/// Pure logic for projecting an active session branch into a runtime <see cref="ConversationState"/>.
/// </summary>
public static class SessionContextBuilder
{
    /// <summary>
    /// Builds a conversation from the active branch, applying the latest compaction overlay.
    /// </summary>
    public static ConversationState Build(IReadOnlyList<SessionEntry> activeBranch, string? skillSystemPrompt)
    {
        ArgumentNullException.ThrowIfNull(activeBranch);

        ConversationState conversation = new();

        if (!string.IsNullOrWhiteSpace(skillSystemPrompt))
        {
            conversation.Add(ConversationMessage.FromText(ConversationRole.System, skillSystemPrompt));
        }

        CompactionSessionEntry? compaction = null;
        for (int index = activeBranch.Count - 1; index >= 0; index--)
        {
            if (activeBranch[index] is CompactionSessionEntry compactionEntry)
            {
                compaction = compactionEntry;
                break;
            }
        }

        int startIndex = 0;
        if (compaction is not null)
        {
            conversation.Add(ConversationMessage.FromText(ConversationRole.System, compaction.Summary));

            for (int index = 0; index < activeBranch.Count; index++)
            {
                if (activeBranch[index].Id == compaction.FirstKeptEntryId)
                {
                    startIndex = index;
                    break;
                }
            }
        }

        for (int index = startIndex; index < activeBranch.Count; index++)
        {
            if (activeBranch[index] is MessageSessionEntry messageEntry)
            {
                conversation.Add(messageEntry.Message);
            }
        }

        return conversation;
    }
}