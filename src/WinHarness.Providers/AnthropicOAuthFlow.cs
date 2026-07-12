using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WinHarness.Providers;

/// <summary>
/// Anthropic (Claude Pro/Max) OAuth PKCE flow and refresh grant.
/// Endpoints, client id, scopes, and callback port verified against pi's
/// shipping implementation (ADR-0005). All vendor knowledge lives here so
/// drift is a single-file fix.
/// </summary>
public sealed class AnthropicOAuthFlow : IOAuthTokenRefresher
{
    // Claude Code client id, base64-obfuscated per ADR-0005.
    private static readonly string ClientId =
        Encoding.UTF8.GetString(Convert.FromBase64String("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl"));

    /// <summary>Authorize endpoint for Claude Pro/Max subscription login.</summary>
    public const string AuthorizeUrl = "https://claude.ai/oauth/authorize";

    /// <summary>Token + refresh endpoint.</summary>
    public const string TokenUrl = "https://platform.claude.com/v1/oauth/token";

    /// <summary>Fixed loopback port registered by Claude Code.</summary>
    public const int CallbackPort = 53692;

    /// <summary>Callback path on the loopback listener.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>Redirect URI sent to Anthropic (localhost, not 127.0.0.1).</summary>
    public static string RedirectUri => $"http://localhost:{CallbackPort}{CallbackPath}";

    /// <summary>OAuth scopes required for inference via the Messages API.</summary>
    public const string Scopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";

    /// <summary>Default Messages API base URL after login.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// Required headers for OAuth-authenticated Messages requests (beta flags
    /// from ADR-0005). API-key requests use a different header set.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OAuthRequestHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic-version"] = "2023-06-01",
            ["anthropic-beta"] = "claude-code-20250219,oauth-2025-04-20",
        };

    /// <summary>Headers for API-key authenticated Messages requests.</summary>
    public static readonly IReadOnlyDictionary<string, string> ApiKeyRequestHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic-version"] = "2023-06-01",
        };

    private readonly HttpClient _http;

    /// <summary>Creates the flow.</summary>
    public AnthropicOAuthFlow(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public string OAuthProviderId => "anthropic";

    /// <summary>
    /// Builds the browser authorize URL and PKCE pair. The caller opens the URL
    /// and either waits on <see cref="WaitForCallbackAsync"/> or accepts a
    /// pasted code/redirect URL.
    /// </summary>
    public AnthropicPkceSession CreatePkceSession()
    {
        string verifier = CreateCodeVerifier();
        string challenge = CreateCodeChallenge(verifier);
        // Pi uses the verifier as the OAuth state so a single secret covers both.
        string state = verifier;

        var query = new Dictionary<string, string>
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        string url = AuthorizeUrl + "?" + string.Join("&", query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new AnthropicPkceSession(verifier, challenge, state, url);
    }

    /// <summary>
    /// Starts a loopback <see cref="HttpListener"/> on the fixed Claude Code
    /// callback port and waits for the authorization code.
    /// </summary>
    public async ValueTask<AnthropicCallbackResult> WaitForCallbackAsync(
        AnthropicPkceSession session,
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
                HttpListenerContext context;
                try
                {
                    context = await getContext.ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return await HandleCallbackAsync(context, session, cancellationToken).ConfigureAwait(false);
            }
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
    /// Parses a pasted authorization code or full redirect URL (manual fallback
    /// when the loopback server is unavailable).
    /// </summary>
    public static AnthropicCallbackResult ParseAuthorizationInput(string input, AnthropicPkceSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        string value = input.Trim();

        string? code = null;
        string? state = null;

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            Dictionary<string, string> query = ParseQuery(uri.Query);
            query.TryGetValue("code", out code);
            query.TryGetValue("state", out state);
        }
        else if (value.Contains('#', StringComparison.Ordinal))
        {
            string[] parts = value.Split('#', 2);
            code = parts[0];
            state = parts.Length > 1 ? parts[1] : null;
        }
        else if (value.Contains("code=", StringComparison.Ordinal))
        {
            string queryText = value.Contains('?', StringComparison.Ordinal)
                ? value[(value.IndexOf('?', StringComparison.Ordinal) + 1)..]
                : value;
            Dictionary<string, string> query = ParseQuery(queryText);
            query.TryGetValue("code", out code);
            query.TryGetValue("state", out state);
        }
        else
        {
            code = value;
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException("Missing authorization code.");
        }

        if (!string.IsNullOrEmpty(state) && !string.Equals(state, session.State, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        return new AnthropicCallbackResult(code, state ?? session.State);
    }

    /// <summary>
    /// Exchanges an authorization code for access + refresh tokens.
    /// </summary>
    public async ValueTask<OAuthTokenSet> ExchangeCodeAsync(
        AnthropicPkceSession session,
        AnthropicCallbackResult callback,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new AnthropicTokenRequest(
                    GrantType: "authorization_code",
                    ClientId: ClientId,
                    Code: callback.Code,
                    State: callback.State,
                    RedirectUri: RedirectUri,
                    CodeVerifier: session.Verifier),
                AnthropicJsonContext.Default.AnthropicTokenRequest),
            Encoding.UTF8,
            "application/json");

        AnthropicTokenResponse token = await SendAsync(
            request,
            AnthropicJsonContext.Default.AnthropicTokenResponse,
            cancellationToken).ConfigureAwait(false);

        return ToTokenSet(token);
    }

    /// <inheritdoc />
    public async ValueTask<OAuthTokenSet> RefreshAsync(OAuthTokenSet current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException(
                "No Anthropic refresh token stored; run 'winharness login --provider anthropic' again.");
        }

        using HttpRequestMessage request = new(HttpMethod.Post, TokenUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new StringContent(
            JsonSerializer.Serialize(
                new AnthropicTokenRequest(
                    GrantType: "refresh_token",
                    ClientId: ClientId,
                    RefreshToken: current.RefreshToken),
                AnthropicJsonContext.Default.AnthropicTokenRequest),
            Encoding.UTF8,
            "application/json");

        AnthropicTokenResponse token = await SendAsync(
            request,
            AnthropicJsonContext.Default.AnthropicTokenResponse,
            cancellationToken).ConfigureAwait(false);

        OAuthTokenSet refreshed = ToTokenSet(token);
        // Preserve refresh token when the endpoint omits a rotation.
        if (string.IsNullOrEmpty(refreshed.RefreshToken))
        {
            refreshed = refreshed with { RefreshToken = current.RefreshToken };
        }

        return refreshed;
    }

    /// <summary>Creates the static Claude model seed used on first login.</summary>
    public static IReadOnlyList<ModelSeed> DefaultModels { get; } =
    [
        new("claude-opus-4", "claude-opus-4-20250514", 200_000, Reasoning: true),
        new("claude-sonnet-4", "claude-sonnet-4-20250514", 200_000, Reasoning: true),
        new("claude-haiku-4-5", "claude-haiku-4-5-20251001", 200_000, Reasoning: false),
    ];

    private static async ValueTask<AnthropicCallbackResult> HandleCallbackAsync(
        HttpListenerContext context,
        AnthropicPkceSession session,
        CancellationToken cancellationToken)
    {
        Uri url = context.Request.Url ?? new Uri(RedirectUri);
        Dictionary<string, string> query = ParseQuery(url.Query);
        query.TryGetValue("error", out string? error);
        query.TryGetValue("code", out string? code);
        query.TryGetValue("state", out string? state);

        if (!string.IsNullOrEmpty(error))
        {
            await WriteHtmlAsync(context.Response, 400, "Anthropic authentication did not complete.", cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException($"Anthropic OAuth failed: {error}");
        }

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            await WriteHtmlAsync(context.Response, 400, "Missing code or state parameter.", cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException("Missing code or state parameter from Anthropic callback.");
        }

        if (!string.Equals(state, session.State, StringComparison.Ordinal))
        {
            await WriteHtmlAsync(context.Response, 400, "State mismatch.", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("OAuth state mismatch.");
        }

        await WriteHtmlAsync(
            context.Response,
            200,
            "Anthropic authentication completed. You can close this window.",
            cancellationToken).ConfigureAwait(false);

        return new AnthropicCallbackResult(code, state);
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

    private static OAuthTokenSet ToTokenSet(AnthropicTokenResponse token)
    {
        if (!string.IsNullOrEmpty(token.Error))
        {
            throw new InvalidOperationException(
                $"Anthropic token request failed: {token.Error}{(string.IsNullOrEmpty(token.ErrorDescription) ? "" : $": {token.ErrorDescription}")}");
        }

        if (string.IsNullOrEmpty(token.AccessToken) || token.ExpiresIn is null)
        {
            throw new InvalidOperationException("Invalid Anthropic token response.");
        }

        return new OAuthTokenSet(
            AccessToken: token.AccessToken,
            RefreshToken: token.RefreshToken,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn.Value),
            Scopes: token.Scope ?? Scopes,
            BaseUrl: DefaultBaseUrl);
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
            ?? throw new InvalidOperationException("Empty response from Anthropic.");
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

/// <summary>PKCE session material for one Anthropic login attempt.</summary>
public sealed record AnthropicPkceSession(
    string Verifier,
    string Challenge,
    string State,
    string AuthorizeUrl);

/// <summary>Authorization code + state returned from the callback or paste path.</summary>
public sealed record AnthropicCallbackResult(string Code, string State);

/// <summary>Static model seed entry written into config on login.</summary>
public sealed record ModelSeed(string Id, string ProviderModelId, int ContextWindow, bool Reasoning);
