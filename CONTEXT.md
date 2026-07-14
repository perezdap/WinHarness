# WinHarness

A Windows-native, terminal-first AI coding harness over OpenAI-compatible model endpoints and MCP stdio tools.

## Language

**Conversation**:
The ordered, provider-neutral list of messages exchanged in one session. Owned by the caller (the REPL or one-shot command), threaded through each turn. Append-only.
_Avoid_: History, session, thread, context (overloaded).

**Turn**:
One run of the agent against a Conversation built from the active session branch (or an ephemeral in-memory list). On completion, the caller appends all turn artifacts to the session when persistence is enabled. A turn may include multiple internal tool round-trips.
_Avoid_: Round, exchange, request.

**Session**:
A persisted JSONL file plus in-memory tree state. Distinct from Conversation (the ephemeral projection sent to the runtime for one turn).
_Avoid_: Thread, chat (overloaded).

**Session entry**:
One append-only JSONL record in a session file. Forms a tree via `id` and `parentId`.
_Avoid_: Message (reserved for ConversationMessage).

**Active branch**:
The path from the current leaf entry to the root. The Conversation for the next turn is built from this path.
_Avoid_: Current thread.

**Turn artifacts**:
All ConversationMessages produced during one agent run: user input, assistant segments (text + tool calls), and tool results. The caller appends these to the session, not just the final assistant text.

**Steering message**:
User input queued while a Turn is running with a `>>` prefix, injected as a user message between tool round-trips within the same Turn. Persisted as a normal user-message artifact. Unconsumed steering is promoted to a Follow-up when the Turn ends.
_Avoid_: Interrupt, injection.

**Follow-up message**:
User input queued while a Turn is running (plain Enter, no prefix), delivered as the next Turn's prompt after the current Turn completes.

**Compaction**:
A session entry that replaces older messages in the *active context* with a summary. Full history remains in the JSONL file. Triggered manually (`/compact`) or automatically (proactive near the model's context window, or reactive retry-once on a provider context-overflow failure).

**Provider**:
A configured model endpoint (id, kind, base URL, optional credential). Kinds: `openai-compatible` and `anthropic-messages / openai-codex-responses`. Distinct from Model.
_Avoid_: Backend, vendor, service.

**Model**:
A configured model alias under a Provider, carrying its own capabilities. The pair (Provider, Model) selects where a Turn runs.
_Avoid_: Engine, deployment.

**Tool**:
A provider-independent capability the model can invoke (built-in file/command tools or MCP stdio tools), exposed through one tool interface.
_Avoid_: Function (reserved for the provider-transport AIFunction), plugin, command.

**Tool filter**:
An optional per-run gating policy over Tools by raw name: allowlist, denylist, or disable-all. Applied when building the model-facing tool list for a Turn; does not affect `tools call` or discovery.
_Avoid_: Permissions, sandbox (different concerns).

**Prompt template**:
A Markdown file with optional YAML frontmatter and `{{placeholder}}` slots, expanded into a Turn's prompt via `/t` or `--template`. `{{input}}` receives trailing free text. Distinct from Skill (persistent per-turn instructions) — a template produces one prompt.
_Avoid_: Macro, snippet.

**Trust decision**:
A persisted always/never choice (trust.json, keyed by normalized path, ancestors cover children) governing whether a workspace's project-local resources (`.winharness\` prompts/skills/SYSTEM.md, `.agents\skills`) load. Untrusted workspaces keep `AGENTS.md` context and global resources.
_Avoid_: Permission, sandbox.

**Auth scheme**:
How a Provider resolves its bearer credential: `api-key` (static secret via credentialName) or `oauth` (token set under `WinHarness:oauth:<provider-id>`, refreshed per request through a flow-specific refresher).
_Avoid_: Login (the command), credential (the stored secret).
