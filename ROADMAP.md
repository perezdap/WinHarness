# WinHarness Roadmap

Feature planning lives in [`docs/design/pi-parity-roadmap.md`](docs/design/pi-parity-roadmap.md),
which tracks three delivery tracks against [pi](https://github.com/earendil-works/pi):

| Track | Scope | Status |
|-------|-------|--------|
| A — Interactive UX (v0.3) | Auto-compaction, steering, tool gating, usage footer, editor input, thinking/model UX, config-dir override | Complete |
| B — OAuth subscription providers (v0.4) | Auth abstraction, `login`/`logout`, Copilot device flow; Anthropic + OpenAI Codex flows | Complete (B0–B4) |
| C — Programmatic modes (v0.5) | Piped stdin, `@file` args, JSON events, prompt templates, project trust, export/import/clone, RPC mode | Complete (C1–C6) |

Architectural decisions are recorded in `docs/adr/` (ADR-0004: MCP as the
extension mechanism; ADR-0005: OAuth subscription providers).

## Shipped items (historical context)

### Anthropic-native provider (Track B, PR-B3 — shipped)

Originally deferred from v0.1 (ADR-0003) because the official `Anthropic` SDK
had unresolved Native AOT concerns. ADR-0005 superseded the SDK question: the
shipped implementation is a hand-rolled, source-generated Messages API client
(`AnthropicMessagesChatClient`) with no SDK dependency — SSE streaming,
`tool_use`/`tool_result` mapping, usage capture, and thinking-budget mapping.
Login uses `AnthropicOAuthFlow` (PKCE) via `winharness login --provider
anthropic`, with a static Claude model seed and
`anthropic-beta: claude-code-20250219,oauth-2025-04-20` headers. The Copilot
flow (OpenAI-compatible) shipped first; this followed under PR-B3.

### OpenAI Codex / Responses API provider (Track B, PR-B4 — shipped)

Same posture as Anthropic: a native Responses API client plus
`chatgpt_account_id` JWT handling, hand-rolled to keep the Native AOT gate
clean. Shipped under PR-B4 alongside the Anthropic transport.

## Deferred items

### Plugin provider implementation

`FuturePluginToolProvider` remains an architectural extension point only.
ADR-0004 rejects runtime plugin loading while the Native AOT gate stands;
MCP servers, skills, and prompt templates are the supported extension
surface. The candidate escape hatch, if hook demand appears, is git-hook
style processes over stdio (future ADR).
