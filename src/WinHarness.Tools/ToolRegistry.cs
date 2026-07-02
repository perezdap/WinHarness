namespace WinHarness.Tools;

/// <summary>
/// Merges tools from configured providers and detects name collisions.
/// </summary>
public sealed class ToolRegistry
{
    private readonly IEnumerable<IToolProvider> _providers;

    /// <summary>
    /// Creates a tool registry.
    /// </summary>
    public ToolRegistry(IEnumerable<IToolProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Lists all tools keyed by name.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<string, ITool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, ITool> tools = new(StringComparer.Ordinal);
        HashSet<string> modelFacingNames = new(StringComparer.Ordinal);

        foreach (IToolProvider provider in _providers)
        {
            IReadOnlyList<ITool> providerTools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (ITool tool in providerTools)
            {
                // Detect collisions on the sanitized, model-facing name so two raw
                // names that collapse to the same string (e.g. "a.b" and "a_b") are
                // rejected here, matching what the runtime exposes. The dictionary
                // stays keyed by the raw tool name so 'tools call'/'tools list' use
                // the names the user actually sees.
                string modelFacingName = ToolNameSanitizer.Sanitize(tool.Name);
                if (!modelFacingNames.Add(modelFacingName))
                {
                    throw new InvalidOperationException($"Duplicate tool name '{tool.Name}' (model-facing '{modelFacingName}').");
                }

                tools[tool.Name] = tool;
            }
        }

        return tools;
    }
}
