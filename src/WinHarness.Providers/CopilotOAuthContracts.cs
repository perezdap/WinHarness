using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinHarness.Providers;

/// <summary>
/// Device-code response from GitHub's device authorization endpoint.
/// </summary>
public sealed record CopilotDeviceCode(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("interval")] int? Interval,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>
/// Access-token polling response (success or RFC 8628 error).
/// </summary>
public sealed record CopilotAccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>
/// Short-lived Copilot bearer from the internal token exchange endpoint.
/// </summary>
public sealed record CopilotBearerResponse(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("expires_at")] long? ExpiresAt);

/// <summary>
/// Source-generated JSON contracts for the Copilot OAuth flow.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CopilotDeviceCode))]
[JsonSerializable(typeof(CopilotAccessTokenResponse))]
[JsonSerializable(typeof(CopilotBearerResponse))]
public sealed partial class CopilotJsonContext : JsonSerializerContext;
