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

        foreach (IToolProvider provider in _providers)
        {
            IReadOnlyList<ITool> providerTools = await provider.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            foreach (ITool tool in providerTools)
            {
                if (!tools.TryAdd(tool.Name, tool))
                {
                    throw new InvalidOperationException($"Duplicate tool name '{tool.Name}'.");
                }
            }
        }

        return tools;
    }
}
