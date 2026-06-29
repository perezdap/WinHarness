using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Best-effort read-only access to OpenRouter's public
/// <c>https://openrouter.ai/api/v1/models</c> catalog. No authentication.
/// Used by <see cref="IModelCapabilityResolver"/> as the Tier 2 cross-reference
/// when an endpoint's own <c>/models</c> response is silent on a capability.
/// </summary>
public interface IOpenRouterModelCatalog
{
    /// <summary>
    /// Returns a case-insensitive index of OpenRouter models keyed by id, or
    /// <c>null</c> when the catalog is unavailable (offline, non-2xx, parse
    /// error, timeout). <c>null</c> means "use the inferrer fallback."
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, OpenRouterModelEntry>?> TryGetIndexAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// A single entry from OpenRouter's <c>/api/v1/models</c>, with derived
/// nullable capability signals. Derived booleans are <c>true</c> when OpenRouter
/// advertises the capability and <c>null</c> otherwise — absence is treated as
/// "unlisted" rather than "unsupported," so Tier 2 only overrides the fallback
/// on a positive signal.
/// </summary>
public sealed record OpenRouterModelEntry(
    string Id,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> SupportedParameters,
    int? ContextLength)
{
    public bool? Vision => InputModalities.Any(static m => m.Equals("image", StringComparison.OrdinalIgnoreCase)) ? true : null;

    public bool? ToolCalling => SupportedParameters.Any(static p => p.Equals("tools", StringComparison.OrdinalIgnoreCase)) ? true : null;

    public bool? StructuredOutput => SupportedParameters.Any(static p => p.Equals("structured_outputs", StringComparison.OrdinalIgnoreCase)) ? true : null;

    public bool? Reasoning => SupportedParameters.Any(static p => p.Equals("reasoning", StringComparison.OrdinalIgnoreCase)) ? true : null;
}

/// <summary>
/// Default implementation. Singleton-cached with a one-hour TTL so a
/// <c>models discover</c> listing 50 models does one OpenRouter fetch, not 50.
/// Any failure returns <c>null</c>; no exceptions leak to the caller.
/// </summary>
public sealed class OpenRouterModelCatalog : IOpenRouterModelCatalog
{
    private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, OpenRouterModelEntry>? _cachedIndex;
    private DateTime _cachedAt;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public OpenRouterModelCatalog()
        : this(new HttpClient())
    {
    }

    public OpenRouterModelCatalog(HttpClient httpClient)
        : this(httpClient, Ttl)
    {
    }

    public OpenRouterModelCatalog(HttpClient httpClient, TimeSpan ttl)
    {
        _httpClient = httpClient;
        _ttl = ttl;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, OpenRouterModelEntry>?> TryGetIndexAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, OpenRouterModelEntry>? snapshot = CurrentSnapshot();
        if (snapshot is not null)
        {
            return snapshot;
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = CurrentSnapshot();
            if (snapshot is not null)
            {
                return snapshot;
            }

            snapshot = await FetchAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                lock (_gate)
                {
                    _cachedIndex = snapshot;
                    _cachedAt = DateTime.UtcNow;
                }
            }

            return snapshot;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private IReadOnlyDictionary<string, OpenRouterModelEntry>? CurrentSnapshot()
    {
        lock (_gate)
        {
            if (_cachedIndex is not null && (DateTime.UtcNow - _cachedAt) < _ttl)
            {
                return _cachedIndex;
            }
        }

        return null;
    }

    private async ValueTask<IReadOnlyDictionary<string, OpenRouterModelEntry>?> FetchAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ModelsUrl);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            OpenRouterModelsResponse? payload = await response.Content
                .ReadFromJsonAsync(OpenRouterJsonContext.Default.OpenRouterModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            if (payload?.Data is null || payload.Data.Count == 0)
            {
                return null;
            }

            Dictionary<string, OpenRouterModelEntry> index = new(StringComparer.OrdinalIgnoreCase);
            foreach (OpenRouterModel dto in payload.Data)
            {
                if (string.IsNullOrWhiteSpace(dto.Id))
                {
                    continue;
                }

                IReadOnlyList<string> inputs = dto.Architecture?.InputModalities ?? [];
                IReadOnlyList<string> parameters = dto.SupportedParameters ?? [];
                int? contextLength = dto.TopProvider?.ContextLength ?? dto.ContextLength;
                OpenRouterModelEntry entry = new(dto.Id!, inputs, parameters, contextLength);
                index[dto.Id!] = entry;

                // Also index the part after the last '/' so an endpoint id like
                // "gpt-4o-mini" matches OpenRouter's "openai/gpt-4o-mini". A
                // real id (or an earlier suffix) wins over a later suffix via
                // TryAdd, so collisions don't overwrite authoritative entries.
                int slash = dto.Id.LastIndexOf('/');
                if (slash >= 0 && slash < dto.Id.Length - 1)
                {
                    string suffix = dto.Id[(slash + 1)..];
                    index.TryAdd(suffix, entry);
                }
            }

            return index;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModel>? Data { get; set; }
}

internal sealed class OpenRouterModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("architecture")]
    public OpenRouterArchitecture? Architecture { get; set; }

    [JsonPropertyName("supported_parameters")]
    public List<string>? SupportedParameters { get; set; }

    [JsonPropertyName("top_provider")]
    public OpenRouterTopProvider? TopProvider { get; set; }
}

internal sealed class OpenRouterArchitecture
{
    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; set; }
}

internal sealed class OpenRouterTopProvider
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OpenRouterModelsResponse))]
internal sealed partial class OpenRouterJsonContext : JsonSerializerContext;
