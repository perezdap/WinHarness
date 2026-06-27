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

**Compaction**:
A session entry that replaces older messages in the *active context* with a summary. Full history remains in the JSONL file.

**Provider**:
A configured OpenAI-compatible endpoint (id, base URL, optional credential). Distinct from Model.
_Avoid_: Backend, vendor, service.

**Model**:
A configured model alias under a Provider, carrying its own capabilities. The pair (Provider, Model) selects where a Turn runs.
_Avoid_: Engine, deployment.

**Tool**:
A provider-independent capability the model can invoke (built-in file/command tools or MCP stdio tools), exposed through one tool interface.
_Avoid_: Function (reserved for the provider-transport AIFunction), plugin, command.
