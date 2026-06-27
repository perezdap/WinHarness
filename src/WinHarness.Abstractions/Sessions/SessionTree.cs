namespace WinHarness.Sessions;

/// <summary>
/// Snapshot of the session entry tree for branch navigation UI.
/// </summary>
public sealed record SessionTree(
    string? LeafEntryId,
    IReadOnlyList<SessionTreeNode> Nodes);

/// <summary>
/// One node in a <see cref="SessionTree"/>.
/// </summary>
public sealed record SessionTreeNode(
    SessionEntry Entry,
    IReadOnlyList<string> ChildIds,
    bool IsOnActiveBranch);