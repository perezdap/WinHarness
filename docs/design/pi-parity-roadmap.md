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

### PR-A5: Editor upgrades (DONE, items 1–3)

Implemented: `EditorInput` helper with (1) `"""` multi-line blocks, (2) `!`
(run + send output to model) / `!!` (run + print only) command escapes through
the captured executor, and (3) `@path` expansion to fenced attachments
(whitespace-anchored regex so emails are ignored; missing paths left alone;
256 KB per-file cap). Deferred: Ctrl+G external editor and image attach — both
fit better once a raw-mode editor exists; interactive fuzzy `@` search likewise.

### PR-A6: Thinking-level UX (DONE)

Implemented: `model:effort` shorthand on `--model-id` and `/model`
(e.g. `gpt-primary:high`, `minimax-m3:cloud:medium`); only known effort levels
(off/minimal/low/medium/high) split, so colon-bearing model ids keep working.
`--reasoning-effort` flag and `/effort` already existed. Per-session effort
changes continue to ride `model_change` entries. Shift+Tab cycling deferred to
PR-A5 raw-mode editor work.

### PR-A7: Model cycling and listing (DONE)

Implemented: `models list --filter <pattern>` — case-insensitive wildcard
(`*`) or substring match over model id and providerModelId, across all
providers or within `--provider-id`. Bare `/model` and `/models` grouped
pickers already existed. Ctrl+P cycling deferred to PR-A5 raw-mode editor work.

### PR-A8: Config directory override (DONE, added post-hoc)

`WINHARNESS_CONFIG_DIR` environment variable redirects the whole configuration
directory (config, sessions, logs, skills). Pi-parity with `PI_CODING_AGENT_DIR`.
Motivated by the AOT gate smoke test touching the real per-user config: the
directory is resolved via the known-folder API, so setting `APPDATA` has no effect.

---

## 4. Track B — OAuth Subscription Providers (v0.4)

The adoption gap: users with Claude Pro/Max, ChatGPT Plus/Pro, or GitHub Copilot subscriptions can't use WinHarness without separate API-key billing.

### 4.1 Reality check (PR-B0: DONE — see ADR-0005)

Flows verified against pi's shipping implementation (`@earendil-works/pi-ai`
`utils/oauth/`): Copilot device flow + `copilot_internal/v2/token` exchange +
OpenAI-compatible proxy baseUrl; Anthropic PKCE via `claude.ai/oauth/authorize`
+ Messages API with `oauth-2025-04-20` beta header; OpenAI Codex PKCE at
`auth.openai.com` (fixed port 1455) + `chatgpt.com/backend-api/codex/responses`.
ToS-risk acceptance, ship order (Copilot → Anthropic → Codex), and drift
containment recorded in `docs/adr/ADR-0005-oauth-subscription-providers.md`.

### 4.2 Auth-scheme abstraction (PR-B1: DONE)

Implemented: `IAuthTokenSource` (per-request resolution) with
`ApiKeyTokenSource` (existing credentialName behavior) and `OAuthTokenSource`
(loads `WinHarness:oauth:<provider-id>` token set, refreshes through a
flow-specific `IOAuthTokenRefresher` when within the 5-minute skew, persists
rotated tokens last-writer-wins). `ProviderOptions.Auth` block
(`scheme: api-key | oauth`, `oauthProvider`) validated by
`WinHarnessOptionsValidator`; `OAuthTokenSet` carries optional `baseUrl`
(Copilot proxy) and `accountId` (Codex JWT claim) for later PRs. Factory
resolves the token source per provider; not-logged-in errors point at
`winharness login`.

### PR-B2: `winharness login` / `logout` (DONE — Copilot)

Implemented: `GitHubCopilotOAuthFlow` (device code start, RFC 8628 polling with
slow_down handling, `copilot_internal/v2/token` bearer exchange, proxy-ep →
baseUrl extraction, enterprise domain support) registered as the first
`IOAuthTokenRefresher`; `login --provider copilot` prints the code, polls,
stores the token set, and auto-creates/updates the `copilot` provider entry;
`login status` lists stored OAuth token sets with expiry; `logout` deletes
them. REPL `/login` deferred — the CLI command works while chat is closed,
which covers the core need. Anthropic/OpenAI flows land with PR-B3/PR-B4.

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

### PR-C1: Piped stdin and `@file` arguments (DONE)

Implemented: redirected stdin is read fully and prepended to `--prompt` as a
fenced ```` ```stdin ```` block; `--files <path>` (repeatable) and inline
`@path` tokens both route through `EditorInput.ExpandFileReferences`, so
limits and formatting match the interactive editor. Missing `--files` paths
error; missing inline tokens are left alone. Image parts deferred until a
vision-capable content-block path exists end to end.

### PR-C2: JSON event stream (`--output json`) (DONE)

Implemented: `chat --prompt "..." --output json` emits LF-delimited JSONL on
stdout (`turn_start`, `assistant_delta`, `tool`, `assistant_message`, `usage`,
`turn_end`, `error`); human output on stderr; nonzero exit on error.
`JsonChatEvent` + source-gen context in Abstractions; contract documented in
`docs/design/json-events.md` (additive-only stability rule). RPC mode (PR-C3)
reuses these event shapes.

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
