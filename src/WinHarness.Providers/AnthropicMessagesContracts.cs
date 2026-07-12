using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Token response from Anthropic's OAuth token endpoint.
/// </summary>
public sealed record AnthropicTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>
/// Token request body for authorization_code or refresh_token grants.
/// Written as a fixed DTO so source-gen serialization stays AOT-safe.
/// </summary>
public sealed record AnthropicTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("code")] string? Code = null,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("redirect_uri")] string? RedirectUri = null,
    [property: JsonPropertyName("code_verifier")] string? CodeVerifier = null,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken = null);

/// <summary>
/// Source-generated JSON contracts for Anthropic OAuth and Messages transport.
/// Request bodies are written with <see cref="Utf8JsonWriter"/> to keep
/// polymorphic content blocks AOT-safe; only fixed DTOs use this context.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicTokenResponse))]
[JsonSerializable(typeof(AnthropicTokenRequest))]
public sealed partial class AnthropicJsonContext : JsonSerializerContext;
