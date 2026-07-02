using System.Text.RegularExpressions;
using WinHarness.Infrastructure.Configuration;

namespace WinHarness.Cli.Chat;

/// <summary>
/// A discovered prompt template: a Markdown file with optional YAML
/// frontmatter (name, description) and {{placeholder}} slots.
/// </summary>
internal sealed record PromptTemplate(
    string Name,
    string Description,
    string FilePath,
    string Body);

/// <summary>
/// Discovers prompt templates and expands placeholders. Discovery mirrors
/// skills: project .winharness/prompts and .agents/prompts (trust-gated),
/// then the global config prompts directory.
/// </summary>
internal static partial class PromptTemplateRegistry
{
    /// <summary>
    /// Placeholder receiving the trailing arguments of an invocation.
    /// </summary>
    public const string InputPlaceholder = "input";

    public static IReadOnlyList<PromptTemplate> Discover(string workspaceRoot, bool includeProjectLocal)
    {
        Dictionary<string, PromptTemplate> templates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in GetTemplateDirectories(workspaceRoot, includeProjectLocal))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly))
            {
                PromptTemplate? template = Load(file);
                if (template is not null)
                {
                    // First discovery wins: project templates shadow global ones.
                    templates.TryAdd(template.Name, template);
                }
            }
        }

        return templates.Values
            .OrderBy(static template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Expands a template: named args fill {{name}} slots, remaining free text
    /// fills {{input}}. Returns the expanded prompt and any unfilled
    /// placeholder names so callers can warn.
    /// </summary>
    public static (string Prompt, IReadOnlyList<string> Missing) Expand(
        PromptTemplate template,
        IReadOnlyDictionary<string, string> namedArgs,
        string freeText)
    {
        HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
        string expanded = PlaceholderPattern().Replace(template.Body, match =>
        {
            string key = match.Groups["name"].Value;
            if (namedArgs.TryGetValue(key, out string? value))
            {
                return value;
            }

            if (string.Equals(key, InputPlaceholder, StringComparison.OrdinalIgnoreCase) && freeText.Length > 0)
            {
                return freeText;
            }

            missing.Add(key);
            return match.Value;
        });

        // Templates without an {{input}} slot still receive trailing text,
        // appended after the body (pi behavior).
        if (freeText.Length > 0 &&
            !PlaceholderPattern().Matches(template.Body)
                .Any(static match => string.Equals(match.Groups["name"].Value, InputPlaceholder, StringComparison.OrdinalIgnoreCase)))
        {
            expanded = expanded.TrimEnd() + Environment.NewLine + Environment.NewLine + freeText;
        }

        return (expanded, [.. missing]);
    }

    /// <summary>
    /// Parses "key=value key2=value2 rest of text" invocation arguments.
    /// Values may be double-quoted to include spaces. Everything that is not
    /// a key=value pair becomes free text for {{input}}.
    /// </summary>
    public static (Dictionary<string, string> Named, string FreeText) ParseArguments(string argumentText)
    {
        Dictionary<string, string> named = new(StringComparer.OrdinalIgnoreCase);
        List<string> free = [];
        foreach (Match token in ArgumentPattern().Matches(argumentText))
        {
            string? key = token.Groups["key"].Success ? token.Groups["key"].Value : null;
            string value = token.Groups["quoted"].Success
                ? token.Groups["quoted"].Value
                : token.Groups["bare"].Value;
            if (key is not null)
            {
                named[key] = value;
            }
            else if (value.Length > 0)
            {
                free.Add(value);
            }
        }

        return (named, string.Join(' ', free));
    }

    private static IEnumerable<string> GetTemplateDirectories(string workspaceRoot, bool includeProjectLocal)
    {
        if (includeProjectLocal)
        {
            yield return Path.Combine(workspaceRoot, ".winharness", "prompts");
            yield return Path.Combine(workspaceRoot, ".agents", "prompts");
        }

        yield return Path.Combine(WinHarnessConfiguration.GetConfigurationDirectory(), "prompts");
    }

    private static PromptTemplate? Load(string filePath)
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

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        (string? name, string? description, string body) = ParseFrontmatter(content);
        string fallbackName = Path.GetFileNameWithoutExtension(filePath);
        return new PromptTemplate(
            (string.IsNullOrWhiteSpace(name) ? fallbackName : name).Trim(),
            (string.IsNullOrWhiteSpace(description) ? "No description." : description).Trim(),
            filePath,
            body.Trim());
    }

    private static (string? Name, string? Description, string Body) ParseFrontmatter(string content)
    {
        using StringReader reader = new(content);
        string? firstLine = reader.ReadLine();
        if (!string.Equals(firstLine, "---", StringComparison.Ordinal))
        {
            return (null, null, content);
        }

        string? name = null;
        string? description = null;
        while (reader.ReadLine() is { } line)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                return (name, description, reader.ReadToEnd() ?? string.Empty);
            }

            int separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        // Unterminated frontmatter: treat the whole file as body.
        return (null, null, content);
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[\w-]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("""(?:(?<key>[\w-]+)=)?(?:"(?<quoted>[^"]*)"|(?<bare>\S+))""", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentPattern();
}
