# Pi Parity Roadmap: Interactive UX, OAuth Providers, and Programmatic Modes

**Status:** Draft
**Target:** WinHarness v0.3 – v0.5
**Goal:** Close the highest-value feature gaps against [pi](https://github.com/earendil-works/pi) while staying true to WinHarness's constraints: Native AOT, Windows-first, MCP as the extension mechanism (no dynamic extension system), and the existing domain language in `CONTEXT.md`.

This plan deliberately **excludes** a pi-style TypeScript extension system. WinHarness's extensibility story is: **MCP servers for tools, skills for instructions, prompt templates for reusable prompts**. That decision should be recorded as an ADR (see PR-0).

---

## 1. Scope Summary

| Track | Features | Target |
|-------|----------|--------|
| A. Interactive UX | Auto-compaction, message queue/steering, editor upgrades (@-file search, multi-line, external editor, `!` bash escape), footer/status line, thinking-level UX, tool gating flags | v0.3 |
| B. OAuth subscription providers | Device/PKCE OAuth flows for Anthropic (Claude Pro/Max), OpenAI (ChatGPT/Codex), GitHub Copilot; token storage/refresh in Credential Manager; `/login`, `/logout` | v0.4 |
| C. Programmatic modes | Piped stdin, `@file` args, `--output json` event stream, RPC mode (stdin/stdout JSONL), session export/import/clone | v0.5 |
| D. Small parity wins | Prompt templates, project trust, model cycling, `--list-models` | folded into A/C |

Out of scope (explicitly rejected or deferred):

- TypeScript/dynamic extension system → **rejected**; MCP is the extension surface.
- Themes and configurable keybindings → deferred; low value for a line-based REPL.
- `/share` (GitHub gist upload) → deferred until export lands and demand exists.
- Sub-agents, plan mode, to-dos → same stance as pi: not in core.

---

## 2. Constraints and Ground Rules

1. **Native AOT gate stays.** Every new dependency must publish `win-x64` Native AOT with zero IL/trim/AOT warnings. OAuth flows must be implementable with `HttpClient` + `System.Text.Json` source-gen — no SDK that fails the gate.
2. **Secrets live in Windows Credential Manager** (`WinHarness:` prefix). OAuth access/refresh tokens are secrets and follow the same rule. On Linux (dev/CI), the existing credential-store fallback applies; tests that need the real store skip on non-Windows.
3. **Domain language:** new concepts get `CONTEXT.md` entries before implementation (e.g. *Steering message*, *Auth scheme*, *Event stream*).
4. **Layering:** provider auth goes in `WinHarness.Providers` + `WinHarness.Infrastructure`; REPL UX stays in `WinHarness.Cli`; protocol/event types in `WinHarness.Abstractions`.
5. **Each PR keeps `dotnet build -c Release` warning-clean and `dotnet test` green** (5 Windows-only skips on Linux remain expected).

---

## 3. Track A — Interactive UX (v0.3)

The daily-driver gap. Ordered by user-felt impact.

### PR-0: ADR — MCP as the extension mechanism

Write `docs/adr/ADR-0004-mcp-as-extension-mechanism.md`:

- **Decision:** WinHarness will not ship a dynamic extension/plugin system. Custom capability = MCP server (stdio or HTTP). Custom instructions = skills. Reusable prompts = prompt templates.
- **Rationale:** Native AOT precludes runtime code loading; MCP already exists in the codebase and is language-agnostic; this inverts pi's "no MCP, extensions instead" philosophy deliberately.
- **Consequences:** UX hooks (custom status lines, permission gates) are not user-extensible in v0.x; revisit only with concrete demand (candidate escape hatch: hook processes over stdio, like git hooks).

### PR-A1: Auto-compaction (DONE)

Implemented: `CompactionOptions` (`autoCompact`, `reserveTokens`) on `WinHarnessOptions`; `AutoCompactionService` in the CLI layer with proactive (chars/4 estimate vs `contextWindow` minus reserve before each turn) and reactive (context-overflow failure message match → compact → retry turn once) triggers; REPL notices when auto-compaction fires; skips ephemeral sessions and respects `autoCompact=false`. Token estimation refinement via real usage data lands with PR-A4.

### PR-A2: Message queue and steering (DONE)

Implemented: `SteeringQueue` in Abstractions; `AgentRunRequest.Steering`;
`TurnRecorderChatClient` drains the queue between tool round-trips (only when
the input ends with a tool result, so steering never splices into a turn's
first request) and records injected messages in turn artifacts. REPL runs the
turn on a background task with a persistent stdin channel: plain lines queue
steering, `>>` prefix queues follow-ups, `/abort` cancels and converts unsent
steering to follow-ups. Line-based (no raw key handling); Esc-abort deferred
until a raw-mode editor exists.

### PR-A3: Tool gating flags (DONE)

Implemented: `--tools`, `--exclude-tools`, `--no-tools` on `winharness chat`; `ToolFilter` record in `WinHarness.Abstractions`; filtering applied in `SingleAgentRuntime.CreateChatOptionsAsync`; unknown names warn; banner shows active gating.

Deferred from original scope: a persistent `tools` block in config for per-workspace policy — revisit alongside PR-C6 (project trust) since workspace-scoped tool policy has the same trust surface.

### PR-A4: Footer/status information (DONE)

Implemented: `UsageFooter` renders a dim one-liner after each persisted-session
turn — `[model @ provider | ctx ~N% (est/window) | turn ↑in ↓out | session ↑in ↓out]` —
from assistant-message `Usage` on the active branch, plus a `/usage` slash
command. Cost display deferred: needs optional per-model `pricing` metadata,
revisit with the OAuth catalogs (Track B) where pricing is known.

### PR-A5: Editor upgrades

Incremental improvements to the REPL input line. Implement in this order:

1. **Multi-line input:** trailing backslash or triple-quote heredoc-style continuation (`"""` ... `"""`). Avoid raw key-handling complexity first pass.
2. **`!command` / `!!command`:** `!` runs via the existing captured `run_command` executor and sends output to the model as a user message; `!!` runs and prints only.
3. **`@` file reference:** on send, expand `@path` tokens to file contents (fenced, with path header). Fuzzy *interactive* search is a later nice-to-have; start with literal path + glob expansion.
4. **Ctrl+G external editor:** write buffer to temp file, launch `$env:VISUAL` → `$env:EDITOR` → `notepad.exe`, read back on exit.
5. **Image attach:** `@image.png` detection by extension → attach as image content part when the active model has `vision`. (Paste-from-clipboard deferred.)

Each numbered item can be its own small PR; 1–2 land with PR-A5, 3–5 may trail.

### PR-A6: Thinking-level UX

`/effort` exists. Round it out:

- `--effort <off|minimal|low|medium|high>` CLI flag for one-shot and REPL startup.
- Model shorthand: `--model gpt-primary:high`.
- Persist per-session effort changes as `model_change`-style session entries (matches existing pattern).

### PR-A7: Model cycling and listing

- `--list-models [pattern]` top-level command output for all configured providers (you have `models list` per provider; add the cross-provider view with wildcard match).
- `/model` with no args → numbered picker across configured models (reuse the wizard's selection UI).
- `--models "pattern1,pattern2"` to scope which models the picker offers.

### PR-A8: Config directory override (DONE, added post-hoc)

`WINHARNESS_CONFIG_DIR` environment variable redirects the whole configuration
directory (config, sessions, logs, skills). Pi-parity with `PI_CODING_AGENT_DIR`.
Motivated by the AOT gate smoke test touching the real per-user config: the
directory is resolved via the known-folder API, so setting `APPDATA` has no effect.

---

## 4. Track B — OAuth Subscription Providers (v0.4)

The adoption gap: users with Claude Pro/Max, ChatGPT Plus/Pro, or GitHub Copilot subscriptions can't use WinHarness without separate API-key billing.

### 4.1 Reality check (do this first, PR-B0)

These flows ride **unofficial/undocumented** endpoints that the vendors ship for their own CLIs (Claude Code, Codex CLI, Copilot). Terms-of-service risk and breakage risk are real; pi and OpenCode accept that risk, and so must we, explicitly:

- **PR-B0 is a spike + ADR**, not feature work. For each of the three providers, verify against current open-source implementations (pi's provider code, OpenCode, Copilot API proxies) what the flow is *today*:
  - **Anthropic (Claude Pro/Max):** OAuth 2.0 + PKCE against `claude.ai` authorize endpoint, token exchange, then Anthropic Messages API with OAuth bearer + the beta header Claude Code sends. **Note: this is the Anthropic-native API, not OpenAI-compatible — see 4.3.**
  - **OpenAI (ChatGPT/Codex):** OAuth + PKCE with local loopback redirect (localhost callback server), tokens scoped to the Codex/Responses backend. Also Responses API, not chat-completions.
  - **GitHub Copilot:** OAuth **device code flow** (no local server needed) → GitHub token → exchange at Copilot's token endpoint for a short-lived bearer → OpenAI-compatible chat-completions at `api.githubcopilot.com` (with required editor headers). **This one lands on the existing OpenAI-compatible path — least new surface.**
- ADR-0005 records: which providers ship in v0.4, the ToS-risk acceptance, and the auth-scheme abstraction below.

**Recommended order: Copilot first** (device flow is simplest, endpoint is chat-completions-compatible, reuses the entire existing provider stack), **then Anthropic, then OpenAI/Codex.**

### 4.2 Auth-scheme abstraction (PR-B1)

Current model: provider → static API key from Credential Manager. Generalize:

```text
ProviderConfig gains:  "auth": { "scheme": "api-key" | "oauth", "oauthProvider": "copilot" | "anthropic" | "openai-codex" }
```

- New abstraction in `WinHarness.Abstractions`: `IAuthTokenSource` with `GetAccessTokenAsync(CancellationToken)` — returns a valid bearer, refreshing transparently.
- `ApiKeyTokenSource` (reads Credential Manager, current behavior) and `OAuthTokenSource` (reads stored token set, refreshes when near expiry, persists rotated refresh tokens back to Credential Manager).
- Token set stored as one JSON secret per provider: `WinHarness:oauth:<provider-id>` → `{ accessToken, refreshToken, expiresAt, scopes }`.
- Provider factory resolves the token source per request instead of a fixed key. Copilot's short-lived bearer (~30 min) makes per-request resolution mandatory, so do it uniformly.
- All JSON via source-generated contexts (AOT).

### PR-B2: `winharness login` / `logout` + `/login` `/logout`

- `winharness login --provider copilot|anthropic|openai` and matching REPL slash commands.
- **Device-code UX (Copilot):** print `Visit https://github.com/login/device and enter code XXXX-XXXX`, poll for completion. No browser automation, no local server. Works over SSH.
- **PKCE + loopback UX (Anthropic, OpenAI):** start `HttpListener` on `127.0.0.1:<ephemeral>`, open browser via `Process.Start` with `UseShellExecute = true` (Windows) and print the URL as fallback, receive the code, exchange, store.
- `winharness login status` — list providers, scheme, token expiry.
- `logout` deletes the Credential Manager entries.
- On successful login, offer to auto-create the provider + known models (subscription endpoints have fixed model lists; ship them as static catalogs updated with releases, like pi does).

### 4.3 Non-OpenAI-compatible transports (PR-B3, the hard one)

Copilot works over the existing OpenAI-compatible pipeline. Anthropic (Messages API) and OpenAI subscription (Responses API) do **not**. Options:

1. **Native minimal clients** — hand-rolled `HttpClient` + source-gen JSON clients for the Anthropic Messages API and the OpenAI Responses API, implementing only what `IChatProvider` needs (streaming, tool calls, usage). No SDK dependency → no AOT risk. This supersedes the ADR-0003 blocker, which was about the *Anthropic SDK's* AOT posture, not the API itself. **Recommended.**
2. Depend on the official Anthropic SDK — re-test AOT posture; historically the blocker (ADR-0003). Only if (1) proves too costly.
3. Ship Copilot-only in v0.4 and defer Anthropic/OpenAI subscription to v0.4.1 — acceptable fallback if the spike shows the native clients are large.

PR-B3 delivers `AnthropicMessagesChatProvider` behind the existing `IChatProvider`/`IProviderFactory` seams; PR-B4 does the same for the Responses API. Each includes streaming, tool-call round-trips, usage capture (feeds PR-A4), and reasoning/thinking parameter mapping (feeds PR-A6).

### 4.4 Failure modes to design in

- Refresh-token rotation races (two WinHarness processes): last-writer-wins on the Credential Manager entry; retry auth once on 401.
- Vendor endpoint/header drift: keep endpoints + required headers in a single static table per provider so a hotfix is one file.
- Clear errors: expired subscription, revoked grant, and rate-limit responses must render as human-readable REPL notices, not stack traces.

---

## 5. Track C — Programmatic Modes (v0.5)

Makes WinHarness embeddable and script-friendly, playing to the CLI-first design.

### PR-C1: Piped stdin and `@file` arguments

- `Get-Content README.md | winharness chat --prompt "Summarize"` — when stdin is redirected, read it fully and prepend to the prompt (fenced block).
- `winharness chat @src\Program.cs @README.md --prompt "Review"` — `@`-prefixed args attach file contents; images become image parts when the model has `vision`. Shares the expansion code with PR-A5 item 3.

### PR-C2: JSON event stream (`--output json`)

- `winharness chat --prompt "..." --output json` emits LF-delimited JSONL events on stdout: `turn_start`, `assistant_delta` (text), `tool_call`, `tool_result`, `assistant_message`, `usage`, `turn_end`, `error`.
- Event types live in `WinHarness.Abstractions` with a source-gen JSON context; the runtime already surfaces these moments internally — this PR is mostly a serializer sink on the existing turn pipeline.
- Contract: stdout carries only JSONL; all human/diagnostic output goes to stderr. Stable `type` discriminator field; document in `docs/design/json-events.md`.

### PR-C3: RPC mode

- `winharness rpc` — long-lived process, JSON-RPC-ish request/response + server-pushed events over stdin/stdout, strict LF framing (mirror pi's warning: no Unicode-separator line splitting).
- Requests: `prompt`, `steer`, `abort`, `set_model`, `set_provider`, `compact`, `get_session`, `list_sessions`, `new_session`, `resume_session`. Responses correlate by `id`; turn events reuse PR-C2's event types.
- This is the embedding story for non-.NET hosts (editors, other agents). Prioritize over any .NET SDK packaging — that audience can already reference the assemblies.
- Ship a tiny reference client script under `samples/`.

### PR-C4: Session export / import / clone

- `/export [file.html|file.jsonl]` and `winharness sessions export --session <id> [--format html|jsonl]`. HTML export = single self-contained file, active branch rendered, tool calls collapsible (`<details>`), no JS dependencies.
- `/import <file.jsonl>` / `--import` — validate entries, copy into the workspace session dir, resume.
- `/clone` — duplicate the active branch into a new session file at the current position (complement to existing `/fork`).

### PR-C5: Prompt templates

- Markdown files with optional YAML frontmatter (`name`, `description`) and `{{placeholder}}` slots.
- Discovery mirrors skills: `.winharness/prompts/`, `%APPDATA%\WinHarness\prompts\`, plus `.agents/prompts/` for cross-tool sharing.
- REPL: `/t <name> [args]` expands into the editor buffer; bare placeholders prompt interactively. `{{input}}` receives trailing args.
- One-shot: `--template review --template-args "focus=security"`.

### PR-C6: Project trust

Security prerequisite that grows in importance as project-local resources multiply.

- On startup in a workspace containing `.winharness\` resources or project-local skills, and no saved decision: prompt `Trust this folder? [always/once/never]`. Persist to `%APPDATA%\WinHarness\trust.json` keyed by normalized path (workspaceKey).
- Untrusted → skip project-local `SYSTEM.md`/`APPEND_SYSTEM.md`, skills, and prompts; still load global resources and plain `AGENTS.md`/`CLAUDE.md` context (informational, matches pi).
- Non-interactive (`--prompt`, `--output json`, `rpc`): never prompt; use `defaultProjectTrust` setting (`ask`→treat as never, `always`, `never`), overridable per run with `--approve` / `--no-approve`.
- `/trust` slash command to save a decision from inside the REPL.

---

## 6. Sequencing and Milestones

```text
v0.3 (Track A)            v0.4 (Track B)             v0.5 (Track C)
──────────────            ──────────────             ──────────────
PR-0   ADR: MCP stance    PR-B0  spike + ADR-0005    PR-C1  stdin + @files
PR-A1  auto-compaction    PR-B1  auth abstraction    PR-C2  JSON events
PR-A2  steering/queue     PR-B2  login/logout        PR-C3  RPC mode
PR-A3  tool gating        PR-B3  Anthropic native    PR-C4  export/import/clone
PR-A4  footer/usage       PR-B4  Responses API       PR-C5  prompt templates
PR-A5  editor upgrades    (Copilot ships with B2)    PR-C6  project trust
PR-A6  thinking UX
PR-A7  model cycling
```

Dependencies worth honoring:

- **PR-A4 before PR-A1** is helpful (usage capture feeds token estimates) but not required — A1 can start with the chars/4 heuristic.
- **PR-B1 before PR-B2**; **PR-B2 (Copilot) is independently shippable** before B3/B4.
- **PR-C2 before PR-C3** (RPC reuses the event schema). **PR-C6 before any feature that executes project-local content in non-interactive modes.**
- PR-A3, A6, A7, C1, C5 are low-risk fillers that can interleave anywhere.

## 7. Definition of Done (per track)

- **Track A:** a long refactoring session runs without manual `/compact`; user can steer mid-turn and abort cleanly; `--tools read_file,grep,glob` gives a genuinely read-only review run; every turn prints usage.
- **Track B:** `winharness login --provider copilot` → `/model` shows Copilot models → chat works with zero API-key config; tokens survive restarts and refresh silently; `login status` is accurate.
- **Track C:** an external process can drive a full multi-turn tool-using session via `winharness rpc` using only stdin/stdout; `--output json` output parses as JSONL with a documented schema; sessions round-trip through export→import.

All of it inside the standing gates: Native AOT publish clean, `TreatWarningsAsErrors` build clean, tests green (expected non-Windows skips only), CONTEXT.md updated for each new domain term, ADRs for the two decisions (MCP stance, OAuth providers).
