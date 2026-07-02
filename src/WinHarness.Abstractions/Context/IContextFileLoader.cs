namespace WinHarness.Context;

/// <summary>
/// Discovers and loads project context files for a workspace root.
/// </summary>
public interface IContextFileLoader
{
    /// <summary>
    /// Loads context files for the given workspace root. Missing files yield empty or null fields.
    /// </summary>
    ProjectContext Load(string workspaceRoot);

    /// <summary>
    /// Loads context files, optionally skipping project-local prompt sources
    /// (.winharness SYSTEM.md / APPEND_SYSTEM.md) for untrusted workspaces.
    /// Plain AGENTS.md/CLAUDE.md context is always loaded (informational).
    /// </summary>
    ProjectContext Load(string workspaceRoot, bool includeProjectLocal) => Load(workspaceRoot);
}