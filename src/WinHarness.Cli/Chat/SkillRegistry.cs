using WinHarness.Infrastructure.Configuration;

namespace WinHarness.Cli.Chat;

internal static class SkillRegistry
{
    public static IReadOnlyList<SkillDefinition> Discover(string workspaceRoot)
    {
        Dictionary<string, SkillDefinition> skills = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in GetSkillDirectories(workspaceRoot))
        {
            LoadDirectory(directory, skills);
        }

        return skills.Values
            .OrderBy(static skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetSkillDirectories(string workspaceRoot)
    {
        yield return Path.Combine(workspaceRoot, ".winharness", "skills");
        yield return Path.Combine(workspaceRoot, ".agents", "skills");
        yield return Path.Combine(WinHarnessConfiguration.GetConfigurationDirectory(), "skills");

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".agents", "skills");
        }
    }

    private static void LoadDirectory(string directory, IDictionary<string, SkillDefinition> skills)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in EnumerateSkillFiles(directory))
        {
            SkillDefinition? skill = TryLoad(file);
            if (skill is not null && !skills.ContainsKey(skill.Name))
            {
                skills.Add(skill.Name, skill);
            }
        }
    }

    private static IEnumerable<string> EnumerateSkillFiles(string directory)
    {
        foreach (string file in SafeEnumerateFiles(directory, "SKILL.md", SearchOption.AllDirectories))
        {
            yield return file;
        }

        foreach (string file in SafeEnumerateFiles(directory, "*.skill.md", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, searchOption).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static SkillDefinition? TryLoad(string filePath)
    {
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        SkillMetadata metadata = ParseMetadata(content);
        string fallbackName = Path.GetFileNameWithoutExtension(Path.GetDirectoryName(filePath) ?? filePath);
        string name = string.IsNullOrWhiteSpace(metadata.Name) ? fallbackName : metadata.Name;
        string description = string.IsNullOrWhiteSpace(metadata.Description) ? "No description." : metadata.Description;
        return new SkillDefinition(name.Trim(), description.Trim(), filePath, content.Trim());
    }

    private static SkillMetadata ParseMetadata(string content)
    {
        string? name = null;
        string? description = null;
        using StringReader reader = new(content);
        string? firstLine = reader.ReadLine();
        if (!string.Equals(firstLine, "---", StringComparison.Ordinal))
        {
            return new SkillMetadata(FindFirstHeading(content), null);
        }

        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                break;
            }

            int separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = Unquote(line[(separator + 1)..].Trim());
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        return new SkillMetadata(name, description);
    }

    private static string? FindFirstHeading(string content)
    {
        using StringReader reader = new(content);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return null;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private sealed record SkillMetadata(string? Name, string? Description);
}
