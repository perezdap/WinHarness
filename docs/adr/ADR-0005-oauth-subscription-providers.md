# ADR-0005: OAuth subscription providers (Copilot, Anthropic, OpenAI Codex)

## Status

Accepted

## Context

WinHarness v0.1–v0.3 requires per-provider API keys. Users with GitHub
Copilot, Claude Pro/Max, or ChatGPT Plus/Pro subscriptions cannot use those
subscriptions. Pi supports all three via OAuth flows against the endpoints
each vendor ships for its own first-party CLI (Copilot, Claude Code, Codex).

These endpoints are **unofficial and undocumented**. The flows below were
verified against pi's currently shipping implementation
(`@earendil-works/pi-ai` `utils/oauth/`), which is actively maintained and is
the de facto reference alongside OpenCode.

### Verified flows (as of this writing)

**GitHub Copilot — device code flow (no local server):**

1. `POST https://github.com/login/device/code` with the VS Code Copilot Chat
   `client_id` and scope `read:user` → `device_code`, `user_code`,
   `verification_uri`, `interval`.
2. User enters the code at github.com/login/device; poll
   `POST https://github.com/login/oauth/access_token` until `access_token`
   (the long-lived "refresh" credential) arrives.
3. Exchange at `GET https://api.github.com/copilot_internal/v2/token` with
   `Authorization: Bearer <gh token>` → short-lived bearer (`token`,
   `expires_at`, ~30 min) plus a per-credential proxy `baseUrl` (e.g.
   `https://api.individual.githubcopilot.com`).
4. Chat completions at that baseUrl, OpenAI-compatible, with required
   headers: `User-Agent: GitHubCopilotChat/<ver>`, `Editor-Version`,
   `Editor-Plugin-Version`, `Copilot-Integration-Id: vscode-chat`.
5. Some models (Claude, Grok) must be enabled once via
   `POST <baseUrl>/models/<id>/policy`. Enterprise domains parameterize all
   URLs. Available model ids come from `<baseUrl>/models`.

**Anthropic (Claude Pro/Max) — PKCE with fixed-port loopback:**

1. Authorize at `https://claude.ai/oauth/authorize` with the Claude Code
   `client_id`, PKCE S256 challenge, redirect
   `http://localhost:<fixed port>/callback`, and scopes including
   `user:inference` and `user:sessions:claude_code`.
2. Exchange the code at `https://platform.claude.com/v1/oauth/token`
   → access + refresh tokens; refresh via the same endpoint.
3. Requests go to the **Anthropic Messages API** with the OAuth bearer and
   `anthropic-beta: claude-code-20250219,oauth-2025-04-20`. Pi also supports
   a manual paste-the-code fallback racing the local server.

**OpenAI (ChatGPT Plus/Pro) — PKCE at `auth.openai.com`:**

1. Authorize with the Codex CLI `client_id`
   (`app_EMoamEEZ73f0CkXaXp7hrann`), PKCE, redirect
   `http://localhost:1455/auth/callback` (fixed port). A device-code
   variant exists via `https://api.openai.com/auth` + a hosted callback.
2. Token exchange yields access/refresh/`id_token`; the JWT carries a
   `chatgpt_account_id` claim required on requests.
3. Requests go to `https://chatgpt.com/backend-api/codex/responses` — the
   **Responses API**, not chat completions — with headers including
   `originator` and `OpenAI-Beta: responses=experimental`.

## Decision

1. **Accept the ToS/breakage risk explicitly.** These are reverse-engineered
   first-party-CLI endpoints. Vendors may change or revoke them at any time;
   accounts could theoretically be actioned. This is the same posture pi and
   OpenCode take. WinHarness will document this in the README section for
   subscription auth.
2. **Ship order: Copilot → Anthropic → OpenAI Codex.**
   - Copilot first: device flow needs no local HTTP server, and the chat
     endpoint is OpenAI-compatible, so it reuses the entire existing
     provider stack. Ships in v0.4.0.
   - Anthropic second: requires a native Messages API client (PR-B3).
   - OpenAI Codex last: requires a Responses API client (PR-B4) and
     JWT-claim handling; the most complex integration.
3. **Auth abstraction (PR-B1):** `IAuthTokenSource` with per-request token
   resolution; `auth` block on provider config
   (`{ "scheme": "api-key" | "oauth", "oauthProvider": "copilot" | ... }`).
   Token sets stored as one JSON secret per provider under
   `WinHarness:oauth:<provider-id>` in Windows Credential Manager.
4. **No new dependencies.** All flows are `HttpClient` +
   `System.Text.Json` source-gen + PKCE via `RandomNumberGenerator`/`SHA256`.
   The Anthropic/OpenAI loopback uses `HttpListener` on the fixed ports the
   vendors' apps register. This keeps the Native AOT gate intact and
   supersedes the ADR-0003 concern, which was about the Anthropic *SDK*,
   not the API.
5. **Vendor drift containment:** endpoints, client ids, scopes, and required
   headers live in one static table per provider so a breakage fix is a
   single-file change. Client ids are stored obfuscated (base64), matching
   pi's practice of not advertising them in plain text.

## Consequences

- v0.4.0 delivers `winharness login --provider copilot` end to end; Anthropic
  and Codex land in v0.4.x behind the same `login` UX as PR-B3/PR-B4 complete.
- The Copilot path must handle: short-lived bearer refresh (~30 min, refresh
  5 min early), per-credential proxy baseUrl from the token response, model
  policy enablement, and the fixed editor headers.
- Two WinHarness processes may race refresh-token rotation; last-writer-wins
  on the credential entry, retry once on 401.
- Clear failure rendering is required: expired subscription, revoked grant,
  and endpoint drift must surface as actionable REPL notices.
- The static model catalogs for subscription providers (fixed model lists per
  plan) ship with releases and may lag vendor changes; Copilot's live
  `/models` listing mitigates this for that provider.
