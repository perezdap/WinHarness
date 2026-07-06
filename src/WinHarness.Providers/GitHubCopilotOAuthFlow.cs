using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinHarness.Providers;

/// <summary>
/// GitHub Copilot OAuth device-code flow and short-lived bearer exchange.
/// Endpoints, client id, and required headers verified against pi's shipping
/// implementation (ADR-0005). All endpoint knowledge lives here so vendor
/// drift is a single-file fix.
/// </summary>
public sealed class GitHubCopilotOAuthFlow : IOAuthTokenRefresher
{
    // VS Code Copilot Chat client id, stored base64-obfuscated per ADR-0005.
    private static readonly string ClientId =
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("SXYxLmI1MDdhMDhjODdlY2ZlOTg="));

    /// <summary>
    /// Required editor headers for every Copilot proxy request (chat and
    /// <c>/models</c> discovery), per ADR-0005. Vendor drift stays in this
    /// file; callers attach these as extra request headers.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> CopilotHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat",
    };

    /// <summary>Fallback base URL when the bearer carries no proxy endpoint.</summary>
    public const string DefaultBaseUrl = "https://api.individual.githubcopilot.com";

    private readonly HttpClient _http;
    private readonly string _domain;

    /// <summary>Creates the flow against github.com or an enterprise domain.</summary>
    public GitHubCopilotOAuthFlow(HttpClient http, string domain = "github.com")
    {
        _http = http;
        _domain = domain;
    }

    /// <inheritdoc />
    public string OAuthProviderId => "copilot";

    /// <summary>
    /// Starts the device flow: returns the user code and verification URI to
    /// display, plus polling parameters.
    /// </summary>
    public async ValueTask<CopilotDeviceCode> StartDeviceFlowAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{_domain}/login/device/code");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", CopilotHeaders["User-Agent"]);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user",
        });

        CopilotDeviceCode device = await SendAsync(
            request,
            CopilotJsonContext.Default.CopilotDeviceCode,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(device.DeviceCode) || string.IsNullOrEmpty(device.UserCode) ||
            !Uri.TryCreate(device.VerificationUri, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("Invalid device code response from GitHub.");
        }

        return device;
    }

    /// <summary>
    /// Polls the access-token endpoint until the user authorizes (RFC 8628:
    /// default 5s interval, +5s on slow_down, deadline from expires_in).
    /// Returns the long-lived GitHub token (the "refresh" credential).
    /// </summary>
    public async ValueTask<string> PollForGitHubTokenAsync(CopilotDeviceCode device, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, device.Interval ?? 5));
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using HttpRequestMessage request = new(HttpMethod.Post, $"https://{_domain}/login/oauth/access_token");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", CopilotHeaders["User-Agent"]);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["device_code"] = device.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });

            CopilotAccessTokenResponse response = await SendAsync(
                request,
                CopilotJsonContext.Default.CopilotAccessTokenResponse,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.AccessToken))
            {
                return response.AccessToken;
            }

            switch (response.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Device flow failed: {response.Error}{(string.IsNullOrEmpty(response.ErrorDescription) ? "" : $": {response.ErrorDescription}")}");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Device flow timed out before the code was authorized.");
    }

    /// <inheritdoc />
    public async ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException("No GitHub token stored; run 'winharness login --provider copilot' again.");
        }

        return await ExchangeForBearerAsync(current.RefreshToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges the long-lived GitHub token for a short-lived Copilot bearer.
    /// The bearer embeds a proxy endpoint that becomes the chat base URL.
    /// </summary>
    public async ValueTask<OAuthTokenSet> ExchangeForBearerAsync(string githubToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.{_domain}/copilot_internal/v2/token");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {githubToken}");
        foreach ((string name, string value) in CopilotHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        CopilotBearerResponse bearer = await SendAsync(
            request,
            CopilotJsonContext.Default.CopilotBearerResponse,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(bearer.Token) || bearer.ExpiresAt is null)
        {
            throw new InvalidOperationException("Invalid Copilot token response.");
        }

        return new OAuthTokenSet(
            AccessToken: bearer.Token,
            RefreshToken: githubToken,
            ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(bearer.ExpiresAt.Value),
            Scopes: "read:user",
            BaseUrl: ExtractBaseUrl(bearer.Token));
    }

    /// <summary>
    /// Extracts the API base URL from the bearer's embedded proxy endpoint
    /// (format: "tid=...;exp=...;proxy-ep=proxy.individual.githubcopilot.com;...").
    /// </summary>
    public static string ExtractBaseUrl(string bearerToken)
    {
        foreach (string part in bearerToken.Split(';'))
        {
            if (part.StartsWith("proxy-ep=", StringComparison.Ordinal))
            {
                string host = part["proxy-ep=".Length..];
                if (host.StartsWith("proxy.", StringComparison.Ordinal))
                {
                    host = "api." + host["proxy.".Length..];
                }

                return $"https://{host}";
            }
        }

        return DefaultBaseUrl;
    }

    private async ValueTask<T> SendAsync<T>(
        HttpRequestMessage request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return JsonSerializer.Deserialize(body, typeInfo)
            ?? throw new InvalidOperationException("Empty response from GitHub.");
    }
}
