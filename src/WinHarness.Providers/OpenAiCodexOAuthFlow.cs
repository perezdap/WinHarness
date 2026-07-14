using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinHarness.Providers;

/// <summary>
/// OpenAI Codex (ChatGPT Plus/Pro) OAuth PKCE flow and refresh grant.
/// Endpoints, client id, scopes, and callback port verified against pi's
/// shipping implementation (ADR-0005). All vendor knowledge lives here so
/// drift is a single-file fix.
/// </summary>
public sealed class OpenAiCodexOAuthFlow : IOAuthTokenRefresher
{
    // Codex CLI client id, base64-obfuscated per ADR-0005.
    private static readonly string ClientId =
        Encoding.UTF8.GetString(Convert.FromBase64String("YXBwX0VNb2FtRUVaNzNmMENrWGFYcDdocmFubg=="));

    /// <summary>Authorize endpoint for ChatGPT / Codex subscription login.</summary>
    public const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";

    /// <summary>Token + refresh endpoint.</summary>
    public const string TokenUrl = "https://auth.openai.com/oauth/token";

    /// <summary>Fixed loopback port registered by Codex CLI.</summary>
    public const int CallbackPort = 1455;

    /// <summary>Callback path on the loopback listener.</summary>
    public const string CallbackPath = "/auth/callback";

    /// <summary>Redirect URI sent to OpenAI (localhost, not 127.0.0.1).</summary>
    public static string RedirectUri => $"http://localhost:{CallbackPort}{CallbackPath}";

    /// <summary>OAuth scopes required for Codex Responses inference.</summary>
    public const string Scopes = "openid profile email offline_access";

    /// <summary>Default Codex backend base URL after login.</summary>
    public const string DefaultBaseUrl = "https://chatgpt.com/backend-api/codex";

    /// <summary>JWT claim namespace that carries chatgpt_account_id.</summary>
    public const string JwtAuthClaimPath = "https://api.openai.com/auth";

    /// <summary>
    /// Originator header value sent on Codex Responses requests and the OAuth
    /// authorize URL. Kept stable so vendor drift is one constant.
    /// </summary>
    public const string Originator = "winharness";

    /// <summary>Required headers for Codex Responses requests (minus auth/account).</summary>
    public static readonly IReadOnlyDictionary<string, string> RequestHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["originator"] = Originator,
            ["OpenAI-Beta"] = "responses=experimental",
        };

    private readonly HttpClient _http;

    /// <summary>Creates the flow.</summary>
    public OpenAiCodexOAuthFlow(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public string OAuthProviderId => ProviderId;

    /// <summary>Canonical oauthProvider / credential provider id for Codex.</summary>
    public const string ProviderId = "openai-codex";

    /// <summary>
    /// Builds the browser authorize URL and PKCE pair. The caller opens the URL
    /// and either waits on <see cref="WaitForCallbackAsync"/> or accepts a
    /// pasted code/redirect URL.
    /// </summary>
    public OpenAiCodexPkceSession CreatePkceSession()
    {
        string verifier = CreateCodeVerifier();
        string challenge = CreateCodeChallenge(verifier);
        string state = CreateState();

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = Originator,
        };

        string url = AuthorizeUrl + "?" + string.Join("&", query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new OpenAiCodexPkceSession(verifier, challenge, state, url);
    }

    /// <summary>
    /// Starts a loopback <see cref="HttpListener"/> on the fixed Codex callback
    /// port and waits for the authorization code.
    /// </summary>
    public async ValueTask<OpenAiCodexCallbackResult> WaitForCallbackAsync(
        OpenAiCodexPkceSession session,
        CancellationToken cancellationToken)
    {
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{CallbackPort}/");
        listener.Prefixes.Add($"http://localhost:{CallbackPort}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Could not bind OAuth callback on port {CallbackPort}. Close anything using that port or paste the redirect URL manually. ({ex.Message})",
                ex);
        }

        try
        {
            Task<HttpListenerContext> getContext = listener.GetContextAsync();
            await using (cancellationToken.Register(static state => ((HttpListener)state!).Stop(), listener))
            {
                HttpListenerContext context = await getContext.ConfigureAwait(false);
                return await HandleCallbackAsync(context, session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// Parses a pasted authorization code or full redirect URL into a callback
    /// result, validating state when present.
    /// </summary>
    public static OpenAiCodexCallbackResult ParseAuthorizationInput(string input, OpenAiCodexPkceSession session)
    {
        string value = input.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("Empty authorization input.");
        }

        string? code = null;
        string? state = null;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? url))
        {
            Dictionary<string, string> query = ParseQuery(url.Query);
            query.TryGetValue("code", out code);
            query.TryGetValue("state", out state);
        }
        else if (value.Contains('#'))
        {
            string[] parts = value.Split('#', 2);
            code = parts[0];
            state = parts.Length > 1 ? parts[1] : null;
        }
        else if (value.Contains("code=", StringComparison.Ordinal))
        {
            Dictionary<string, string> query = ParseQuery(value);
            query.TryGetValue("code", out code);
            query.TryGetValue("state", out state);
        }
        else
        {
            code = value;
            state = session.State;
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("Could not find an authorization code in the pasted input.");
        }

        if (!string.IsNullOrEmpty(state) &&
            !string.Equals(state, session.State, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        return new OpenAiCodexCallbackResult(code, state ?? session.State);
    }

    /// <summary>
    /// Exchanges an authorization code for access + refresh tokens and extracts
    /// the ChatGPT account id from the access JWT.
    /// </summary>
    public async ValueTask<OAuthTokenSet> ExchangeCodeAsync(
        OpenAiCodexPkceSession session,
        OpenAiCodexCallbackResult callback,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = callback.Code,
            ["code_verifier"] = session.Verifier,
            ["redirect_uri"] = RedirectUri,
        });

        OpenAiCodexTokenResponse token = await SendAsync(
            request,
            OpenAiCodexJsonContext.Default.OpenAiCodexTokenResponse,
            cancellationToken).ConfigureAwait(false);

        return ToTokenSet(token);
    }

    /// <inheritdoc />
    public async ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException(
                "No OpenAI Codex refresh token stored; run 'winharness login --provider openai' again.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["client_id"] = ClientId,
        });

        OpenAiCodexTokenResponse token = await SendAsync(
            request,
            OpenAiCodexJsonContext.Default.OpenAiCodexTokenResponse,
            cancellationToken).ConfigureAwait(false);

        OAuthTokenSet refreshed = ToTokenSet(token, requireRefreshToken: false, fallbackAccountId: current.AccountId);
        if (string.IsNullOrEmpty(refreshed.RefreshToken))
        {
            refreshed = refreshed with { RefreshToken = current.RefreshToken };
        }

        if (string.IsNullOrEmpty(refreshed.AccountId))
        {
            refreshed = refreshed with { AccountId = current.AccountId };
        }

        return refreshed;
    }

    /// <summary>Creates the static Codex model seed used on first login.</summary>
    public static IReadOnlyList<ModelSeed> DefaultModels { get; } =
    [
        new("gpt-5.4", "gpt-5.4", 272_000, Reasoning: true),
        new("gpt-5.4-mini", "gpt-5.4-mini", 272_000, Reasoning: true),
        new("gpt-5.5", "gpt-5.5", 272_000, Reasoning: true),
    ];

    /// <summary>
    /// Extracts <c>chatgpt_account_id</c> from an access JWT. Returns null when
    /// the claim is missing or the token is not a JWT.
    /// </summary>
    public static string? ExtractAccountId(string accessToken)
    {
        try
        {
            string[] parts = accessToken.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            string payload = parts[1];
            int pad = payload.Length % 4;
            if (pad > 0)
            {
                payload += new string('=', 4 - pad);
            }

            payload = payload.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(payload);
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty(JwtAuthClaimPath, out JsonElement auth) ||
                !auth.TryGetProperty("chatgpt_account_id", out JsonElement accountId))
            {
                return null;
            }

            return accountId.GetString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async ValueTask<OpenAiCodexCallbackResult> HandleCallbackAsync(
        HttpListenerContext context,
        OpenAiCodexPkceSession session,
        CancellationToken cancellationToken)
    {
        Uri url = context.Request.Url ?? new Uri(RedirectUri);
        Dictionary<string, string> query = ParseQuery(url.Query);
        query.TryGetValue("error", out string? error);
        query.TryGetValue("code", out string? code);
        query.TryGetValue("state", out string? state);

        if (!string.IsNullOrEmpty(error))
        {
            await WriteHtmlAsync(context.Response, 400, "OpenAI authentication did not complete.", cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException($"OpenAI OAuth failed: {error}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await WriteHtmlAsync(context.Response, 400, "Missing code or state parameter.", cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("Missing code or state parameter from OpenAI callback.");
        }

        if (!string.Equals(state, session.State, StringComparison.Ordinal))
        {
            await WriteHtmlAsync(context.Response, 400, "State mismatch.", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        await WriteHtmlAsync(
            context.Response,
            200,
            "OpenAI authentication completed. You can close this window.",
            cancellationToken).ConfigureAwait(false);

        return new OpenAiCodexCallbackResult(code, state);
    }

    private static async ValueTask WriteHtmlAsync(
        HttpListenerResponse response,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><body><p>{WebUtility.HtmlEncode(message)}</p></body></html>");
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static OAuthTokenSet ToTokenSet(
        OpenAiCodexTokenResponse token,
        bool requireRefreshToken = true,
        string? fallbackAccountId = null)
    {
        if (!string.IsNullOrEmpty(token.Error))
        {
            throw new InvalidOperationException(
                $"OpenAI token request failed: {token.Error}{(string.IsNullOrEmpty(token.ErrorDescription) ? "" : $": {token.ErrorDescription}")}");
        }

        if (string.IsNullOrEmpty(token.AccessToken) || token.ExpiresIn is null)
        {
            throw new InvalidOperationException("Invalid OpenAI Codex token response.");
        }

        if (requireRefreshToken && string.IsNullOrEmpty(token.RefreshToken))
        {
            throw new InvalidOperationException("Invalid OpenAI Codex token response (missing refresh_token).");
        }

        string? accountId = ExtractAccountId(token.AccessToken) ?? fallbackAccountId;
        if (string.IsNullOrEmpty(accountId))
        {
            throw new InvalidOperationException(
                "OpenAI access token did not contain a chatgpt_account_id claim. Re-run login.");
        }

        return new OAuthTokenSet(
            AccessToken: token.AccessToken,
            RefreshToken: token.RefreshToken,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn.Value),
            Scopes: Scopes,
            BaseUrl: DefaultBaseUrl,
            AccountId: accountId);
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
            ?? throw new InvalidOperationException("Empty response from OpenAI.");
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string CreateState()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        string text = query.StartsWith('?') ? query[1..] : query;
        foreach (string pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                result[Uri.UnescapeDataString(pair.Replace('+', ' '))] = string.Empty;
                continue;
            }

            string key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}

/// <summary>PKCE session material for one OpenAI Codex login attempt.</summary>
public sealed record OpenAiCodexPkceSession(
    string Verifier,
    string Challenge,
    string State,
    string AuthorizeUrl);

/// <summary>Authorization code + state returned from the callback or paste path.</summary>
public sealed record OpenAiCodexCallbackResult(string Code, string State);

/// <summary>Token response from OpenAI's OAuth token endpoint.</summary>
public sealed record OpenAiCodexTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>Source-generated JSON contracts for OpenAI Codex OAuth.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpenAiCodexTokenResponse))]
public sealed partial class OpenAiCodexJsonContext : JsonSerializerContext;
