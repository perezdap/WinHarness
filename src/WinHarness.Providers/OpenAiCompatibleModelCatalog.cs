using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Queries the OpenAI-compatible <c>GET /models</c> endpoint to discover the
/// models a provider advertises. Uses source-generated JSON so it stays Native
/// AOT safe. The base URL is treated as the OpenAI-compatible root and
/// <c>/models</c> is appended.
/// </summary>
public sealed class OpenAiCompatibleModelCatalog : IModelCatalog
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates a catalog with a dedicated <see cref="HttpClient"/>.
    /// </summary>
    public OpenAiCompatibleModelCatalog()
        : this(new HttpClient())
    {
    }

    /// <summary>
    /// Creates a catalog over a caller-supplied <see cref="HttpClient"/>.
    /// </summary>
    public OpenAiCompatibleModelCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CatalogModel>> ListModelsAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        Uri endpoint = BuildModelsUri(baseUrl);
        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Model discovery failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
        }

        ModelListResponse? payload = await response.Content
            .ReadFromJsonAsync(ModelCatalogJsonContext.Default.ModelListResponse, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Data is null || payload.Data.Count == 0)
        {
            return [];
        }

        return payload.Data
            .Where(static model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(static model =>
            {
                int? contextWindow = model.ContextWindow ?? model.ContextLength ?? model.TopProvider?.ContextLength;
                List<string>? efforts = model.ModelSpec?.Capabilities?.ReasoningEffortOptions ?? model.Reasoning?.SupportedEfforts;
                bool? reasoning = model.ModelSpec?.Capabilities?.SupportsReasoning;
                if (reasoning is null && efforts is { Count: > 0 })
                {
                    reasoning = true;
                }

                bool? vision = model.ModelSpec?.Capabilities?.SupportsVision;
                if (vision is null)
                {
                    if (model.Architecture?.InputModalities is { Count: > 0 } inputs &&
                        inputs.Any(static m => m.Equals("image", StringComparison.OrdinalIgnoreCase)))
                    {
                        vision = true;
                    }
                    else if (model.Architecture?.Modality is string modality)
                    {
                        vision = modality.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                                 modality.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
                    }
                }

                bool? toolCalling = model.ModelSpec?.Capabilities?.SupportsFunctionCalling;
                if (toolCalling is null &&
                    model.SupportedParameters is { Count: > 0 } toolParams &&
                    toolParams.Any(static p => p.Equals("tools", StringComparison.OrdinalIgnoreCase)))
                {
                    toolCalling = true;
                }

                bool? structuredOutput = model.ModelSpec?.Capabilities?.SupportsResponseSchema;
                if (structuredOutput is null &&
                    model.SupportedParameters is { Count: > 0 } structParams &&
                    structParams.Any(static p => p.Equals("structured_outputs", StringComparison.OrdinalIgnoreCase)))
                {
                    structuredOutput = true;
                }

                // Venice advertises prompt caching through the presence of a
                // cache_input price (always non-zero when present in the live
                // response). OpenRouter's /models schema has no equivalent
                // signal, so PromptCaching stays null there and falls through
                // to the name heuristic in the inferrer.
                bool? promptCaching = model.ModelSpec?.Pricing?.CacheInput is not null ? true : null;

                return new CatalogModel(
                    model.Id!,
                    model.OwnedBy,
                    contextWindow,
                    vision,
                    efforts,
                    reasoning,
                    toolCalling,
                    structuredOutput,
                    promptCaching);
            })
            .DistinctBy(static model => model.Id, StringComparer.Ordinal)
            .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Uri BuildModelsUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException($"Base URL '{baseUrl}' is not an absolute URI.");
        }

        string trimmed = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(trimmed + "/models");
    }

    private static string Truncate(string value)
    {
        const int Max = 200;
        value = value.Trim();
        return value.Length <= Max ? value : value[..Max] + "…";
    }
}

internal sealed class ModelListResponse
{
    [JsonPropertyName("data")]
    public List<ModelListEntry>? Data { get; set; }
}

internal sealed class ModelListEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; set; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("context_window")]
    public int? ContextWindow { get; set; }

    [JsonPropertyName("architecture")]
    public ModelArchitecture? Architecture { get; set; }

    [JsonPropertyName("reasoning")]
    public ModelReasoning? Reasoning { get; set; }

    [JsonPropertyName("supported_parameters")]
    public List<string>? SupportedParameters { get; set; }

    [JsonPropertyName("top_provider")]
    public ModelTopProvider? TopProvider { get; set; }

    [JsonPropertyName("model_spec")]
    public VeniceModelSpec? ModelSpec { get; set; }
}

internal sealed class ModelArchitecture
{
    [JsonPropertyName("modality")]
    public string? Modality { get; set; }

    [JsonPropertyName("input_modalities")]
    public List<string>? InputModalities { get; set; }

    [JsonPropertyName("output_modalities")]
    public List<string>? OutputModalities { get; set; }
}

internal sealed class ModelReasoning
{
    [JsonPropertyName("supported_efforts")]
    public List<string>? SupportedEfforts { get; set; }
}

internal sealed class ModelTopProvider
{
    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }
}

// Venice's /api/v1/models response nests capability signals under
// model_spec.capabilities with Venice-specific boolean field names, plus a
// pricing.cache_input object whose presence is a reliable prompt-caching
// signal. None of these match the OpenRouter schema; both are read here so
// the endpoint parser handles Venice and OpenRouter-style endpoints alike.
internal sealed class VeniceModelSpec
{
    [JsonPropertyName("capabilities")]
    public VeniceCapabilities? Capabilities { get; set; }

    [JsonPropertyName("pricing")]
    public VenicePricing? Pricing { get; set; }
}

internal sealed class VeniceCapabilities
{
    [JsonPropertyName("supportsVision")]
    public bool? SupportsVision { get; set; }

    [JsonPropertyName("supportsFunctionCalling")]
    public bool? SupportsFunctionCalling { get; set; }

    [JsonPropertyName("supportsResponseSchema")]
    public bool? SupportsResponseSchema { get; set; }

    [JsonPropertyName("supportsReasoning")]
    public bool? SupportsReasoning { get; set; }

    [JsonPropertyName("reasoningEffortOptions")]
    public List<string>? ReasoningEffortOptions { get; set; }
}

internal sealed class VenicePricing
{
    [JsonPropertyName("cache_input")]
    public VeniceCacheInputPrice? CacheInput { get; set; }
}

internal sealed class VeniceCacheInputPrice
{
    [JsonPropertyName("usd")]
    public decimal? Usd { get; set; }

    [JsonPropertyName("diem")]
    public decimal? Diem { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ModelListResponse))]
internal sealed partial class ModelCatalogJsonContext : JsonSerializerContext;
