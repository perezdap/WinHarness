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
            .Select(static model => new CatalogModel(model.Id!, model.OwnedBy))
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
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ModelListResponse))]
internal sealed partial class ModelCatalogJsonContext : JsonSerializerContext;
