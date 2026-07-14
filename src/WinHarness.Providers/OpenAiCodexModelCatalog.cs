using System.Net.Http.Headers;
using System.Text.Json;

namespace WinHarness.Providers;

/// <summary>
/// Reads the Codex subscription model catalog. The ChatGPT backend returns a
/// <c>{ models: [...] }</c> payload rather than the OpenAI API's
/// <c>{ data: [...] }</c> shape, and requires account-scoped request headers.
/// </summary>
public sealed class OpenAiCodexModelCatalog
{
    // This is the Codex protocol's client version, not WinHarness's product
    // version. The ChatGPT backend currently accepts the official Codex
    // development version (0.0.0); sending WinHarness's version returns an
    // empty catalog with HTTP 200.
    private const string ClientVersion = "0.0.0";

    private readonly HttpClient _httpClient;

    /// <summary>Creates a catalog with a dedicated <see cref="HttpClient"/>.</summary>
    public OpenAiCodexModelCatalog()
        : this(new HttpClient())
    {
    }

    /// <summary>Creates a catalog over a caller-supplied <see cref="HttpClient"/>.</summary>
    public OpenAiCodexModelCatalog(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Lists the models visible to one authenticated Codex account.</summary>
    public async ValueTask<IReadOnlyList<CatalogModel>> ListModelsAsync(
        string baseUrl,
        string accessToken,
        string accountId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        using HttpRequestMessage request = new(HttpMethod.Get, BuildModelsUri(baseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", accountId);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Codex model discovery failed: {(int)response.StatusCode} {response.ReasonPhrase}. {Truncate(body)}");
        }

        return ParseModels(body);
    }

    internal static IReadOnlyList<CatalogModel> ParseModels(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("models", out JsonElement models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Codex model discovery returned no models array.");
        }

        List<CatalogModel> result = [];
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("slug", out JsonElement slugElement) ||
                slugElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? slug = slugElement.GetString();
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            if (model.TryGetProperty("visibility", out JsonElement visibility) &&
                visibility.ValueKind == JsonValueKind.String &&
                !string.Equals(visibility.GetString(), "list", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> reasoningEfforts = [];
            if (model.TryGetProperty("supported_reasoning_levels", out JsonElement levels) &&
                levels.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement level in levels.EnumerateArray())
                {
                    if (level.TryGetProperty("effort", out JsonElement effort) &&
                        effort.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(effort.GetString()))
                    {
                        reasoningEfforts.Add(effort.GetString()!);
                    }
                }
            }

            if (reasoningEfforts.Count == 0 &&
                model.TryGetProperty("default_reasoning_level", out JsonElement defaultLevel) &&
                defaultLevel.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(defaultLevel.GetString()))
            {
                reasoningEfforts.Add(defaultLevel.GetString()!);
            }

            bool? vision = null;
            if (model.TryGetProperty("input_modalities", out JsonElement modalities) &&
                modalities.ValueKind == JsonValueKind.Array)
            {
                vision = modalities.EnumerateArray().Any(static modality =>
                    modality.ValueKind == JsonValueKind.String &&
                    string.Equals(modality.GetString(), "image", StringComparison.OrdinalIgnoreCase));
            }

            int? contextWindow = null;
            if (model.TryGetProperty("context_window", out JsonElement context) &&
                context.TryGetInt32(out int parsedContext))
            {
                contextWindow = parsedContext;
            }

            result.Add(new CatalogModel(
                slug,
                OwnedBy: "openai-codex",
                ContextWindow: contextWindow,
                Vision: vision,
                SupportedReasoningEfforts: reasoningEfforts.Count == 0 ? null : reasoningEfforts,
                Reasoning: reasoningEfforts.Count > 0,
                ToolCalling: true,
                StructuredOutput: false,
                PromptCaching: true));
        }

        return result
            .DistinctBy(static model => model.Id, StringComparer.Ordinal)
            .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Uri BuildModelsUri(string baseUrl)
    {
        string normalized = baseUrl.TrimEnd('/');
        if (!normalized.EndsWith("/codex", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/codex";
        }

        return new($"{normalized}/models?client_version={Uri.EscapeDataString(ClientVersion)}");
    }

    private static string Truncate(string value)
    {
        const int maxLength = 200;
        value = value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
