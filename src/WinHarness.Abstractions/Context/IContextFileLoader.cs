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
}