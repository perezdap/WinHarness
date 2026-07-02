namespace WinHarness.Tools;

/// <summary>
/// Optional per-run tool gating policy. Names match the raw tool name
/// (e.g. "read_file", "filesystem.list_dir") case-insensitively.
/// Precedence: DisableAll, then Allow (when non-empty, only listed names
/// are enabled), then Exclude.
/// </summary>
public sealed record ToolFilter(
    IReadOnlyList<string>? Allow = null,
    IReadOnlyList<string>? Exclude = null,
    bool DisableAll = false)
{
    /// <summary>
    /// Returns whether a tool with the given raw name is enabled under this filter.
    /// </summary>
    public bool IsEnabled(string toolName)
    {
        if (DisableAll)
        {
            return false;
        }

        if (Allow is { Count: > 0 } &&
            !Allow.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Exclude is { Count: > 0 } &&
            Exclude.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
