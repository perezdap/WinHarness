using System.Text.Json;
using WinHarness.Serialization;

namespace WinHarness.Infrastructure.Configuration;

/// <summary>
/// Persists project trust decisions in trust.json under the configuration
/// directory, keyed by normalized absolute workspace path. A decision on a
/// folder applies to all folders beneath it.
/// </summary>
public sealed class TrustStore
{
    private readonly string _filePath;

    /// <summary>Creates a store in the configuration directory.</summary>
    public TrustStore(string? configurationDirectory = null)
    {
        string directory = configurationDirectory ?? WinHarnessConfiguration.GetConfigurationDirectory();
        _filePath = Path.Combine(directory, "trust.json");
    }

    /// <summary>
    /// Returns the saved decision covering the workspace (exact path or any
    /// ancestor), or null when undecided.
    /// </summary>
    public bool? GetDecision(string workspaceRoot)
    {
        Dictionary<string, string> decisions = Load();
        string current = Normalize(workspaceRoot);
        while (true)
        {
            if (decisions.TryGetValue(current, out string? decision))
            {
                return string.Equals(decision, "always", StringComparison.OrdinalIgnoreCase);
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
            {
                return null;
            }

            current = parent;
        }
    }

    /// <summary>Saves an always/never decision for the workspace.</summary>
    public void SaveDecision(string workspaceRoot, bool trusted)
    {
        Dictionary<string, string> decisions = Load();
        decisions[Normalize(workspaceRoot)] = trusted ? "always" : "never";
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(decisions, WinHarnessJsonSerializerContext.Default.DictionaryStringString));
    }

    /// <summary>
    /// Whether the workspace contains project-local resources that warrant a
    /// trust decision (prompt-injection surface): .winharness\ or .agents\skills.
    /// </summary>
    public static bool HasProjectLocalResources(string workspaceRoot)
    {
        return Directory.Exists(Path.Combine(workspaceRoot, ".winharness")) ||
            Directory.Exists(Path.Combine(workspaceRoot, ".agents", "skills"));
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string>? parsed = JsonSerializer.Deserialize(
            File.ReadAllText(_filePath),
            WinHarnessJsonSerializerContext.Default.DictionaryStringString);
        return parsed is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
