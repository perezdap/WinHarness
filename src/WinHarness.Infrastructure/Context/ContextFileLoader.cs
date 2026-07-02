using WinHarness.Context;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.Infrastructure.Context;

/// <summary>
/// Discovers AGENTS.md, CLAUDE.md, SYSTEM.md, and APPEND_SYSTEM.md per the Phase 1 context file rules.
/// </summary>
public sealed class ContextFileLoader : IContextFileLoader
{
    private const string AgentsSeparator = "\n\n---\n\n";
    private static readonly string[] AgentFileNames = ["AGENTS.md", "CLAUDE.md"];

    private readonly string _globalConfigDirectory;

    /// <summary>
    /// Creates a loader using the WinHarness configuration directory for global context files.
    /// </summary>
    public ContextFileLoader(string? globalConfigDirectory = null)
    {
        _globalConfigDirectory = globalConfigDirectory ?? WinHarnessConfiguration.GetConfigurationDirectory();
    }

    /// <inheritdoc />
    public ProjectContext Load(string workspaceRoot)
    {
        return Load(workspaceRoot, includeProjectLocal: true);
    }

    /// <inheritdoc />
    public ProjectContext Load(string workspaceRoot, bool includeProjectLocal)
    {
        string cwd = Path.GetFullPath(workspaceRoot);
        return new ProjectContext(
            LoadSystemPromptReplacement(cwd, includeProjectLocal),
            LoadSystemPromptAppend(cwd, includeProjectLocal),
            LoadAgentsInstructions(cwd));
    }

    private string? LoadSystemPromptReplacement(string cwd, bool includeProjectLocal)
    {
        return includeProjectLocal
            ? ReadFirstExisting(
                Path.Combine(cwd, ".winharness", "SYSTEM.md"),
                Path.Combine(_globalConfigDirectory, "SYSTEM.md"))
            : ReadFirstExisting(Path.Combine(_globalConfigDirectory, "SYSTEM.md"));
    }

    private string? LoadSystemPromptAppend(string cwd, bool includeProjectLocal)
    {
        return includeProjectLocal
            ? ReadFirstExisting(
                Path.Combine(cwd, ".winharness", "APPEND_SYSTEM.md"),
                Path.Combine(_globalConfigDirectory, "APPEND_SYSTEM.md"))
            : ReadFirstExisting(Path.Combine(_globalConfigDirectory, "APPEND_SYSTEM.md"));
    }

    private string LoadAgentsInstructions(string cwd)
    {
        List<string> parts = [];

        string? globalAgents = ReadFileIfExists(Path.Combine(_globalConfigDirectory, "AGENTS.md"));
        if (globalAgents is not null)
        {
            parts.Add(globalAgents);
        }

        foreach (string directory in EnumerateDirectoriesFromDriveRootTo(cwd))
        {
            foreach (string fileName in AgentFileNames)
            {
                string? content = ReadFileIfExists(Path.Combine(directory, fileName));
                if (content is not null)
                {
                    parts.Add(content);
                }
            }
        }

        return parts.Count == 0 ? string.Empty : string.Join(AgentsSeparator, parts);
    }

    private static string? ReadFirstExisting(params string[] paths)
    {
        foreach (string path in paths)
        {
            string? content = ReadFileIfExists(path);
            if (content is not null)
            {
                return content;
            }
        }

        return null;
    }

    private static string? ReadFileIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
    }

    private static IEnumerable<string> EnumerateDirectoriesFromDriveRootTo(string workspaceRoot)
    {
        string fullPath = Path.GetFullPath(workspaceRoot);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Unable to determine drive root for '{workspaceRoot}'.");

        string current = root;
        yield return current;

        string relative = Path.GetRelativePath(root, fullPath);
        if (string.IsNullOrEmpty(relative) || relative == ".")
        {
            yield break;
        }

        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            yield return current;
        }
    }
}