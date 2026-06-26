# WinHarness

A Windows-native, terminal-first AI coding harness over OpenAI-compatible model endpoints and MCP stdio tools.

## Language

**Conversation**:
The ordered, provider-neutral list of messages exchanged in one session. Owned by the caller (the REPL or one-shot command), threaded through each turn. Append-only.
_Avoid_: History, session, thread, context (overloaded).

**Turn**:
One run of the agent against the current Conversation: the runtime sends the messages, streams the reply, and reports the final assistant message for the caller to append. A turn may run tool round-trips internally but stores only the final assistant text.
_Avoid_: Round, exchange, request.

**Provider**:
A configured OpenAI-compatible endpoint (id, base URL, optional credential). Distinct from Model.
_Avoid_: Backend, vendor, service.

**Model**:
A configured model alias under a Provider, carrying its own capabilities. The pair (Provider, Model) selects where a Turn runs.
_Avoid_: Engine, deployment.

**Tool**:
A provider-independent capability the model can invoke (built-in file/command tools or MCP stdio tools), exposed through one tool interface.
_Avoid_: Function (reserved for the provider-transport AIFunction), plugin, command.
