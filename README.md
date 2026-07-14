# WinHarness

WinHarness is a Windows-native, terminal-first AI coding harness designed around Native AOT, MCP stdio tools, and OpenAI-compatible model endpoints.

## v0.1 and v0.2 scope

**v0.2** adds persistent tree-structured sessions, full turn artifacts (including tool calls and results), project context files (`AGENTS.md`, `SYSTEM.md`), and manual compaction. Interactive `winharness chat` continues the most recent workspace session by default.

**v0.1** provider scope is unchanged — OpenAI-compatible endpoints only:

- OpenAI
- Ollama `/v1`
- LM Studio `/v1`
- vLLM `/v1`
- OpenRouter-compatible endpoints
- other compatible `/v1` chat-completions style APIs

Anthropic Messages and OpenAI Codex Responses are supported via hand-rolled AOT-safe clients (ADR-0005); use `winharness login --provider anthropic` or `openai` for subscription auth.

## Native AOT gate

Phase 0 is a dependency spike, not application feature work. It validates that the selected v0.1 dependency set can:

- restore and build on .NET 10,
- publish as a single `win-x64` Native AOT executable,
- emit zero IL/trimming/AOT warnings,
- run representative paths in the published binary.

The spike is located in `spikes/WinHarness.AotSpike`.

## Build prerequisites

- .NET 10 SDK
- Windows x64 for the Native AOT publish/run gate

## Phase 0 commands

```powershell
dotnet restore .\spikes\WinHarness.AotSpike\WinHarness.AotSpike.csproj
dotnet build .\spikes\WinHarness.AotSpike\WinHarness.AotSpike.csproj -c Release --no-restore
dotnet publish .\spikes\WinHarness.AotSpike\WinHarness.AotSpike.csproj -c Release -r win-x64 --no-build /p:PublishAot=true
.\spikes\WinHarness.AotSpike\bin\Release\net10.0\win-x64\publish\winharness-aot-spike.exe
```

## CLI smoke commands

```powershell
dotnet publish .\src\WinHarness.Cli\WinHarness.Cli.csproj -c Release -r win-x64 --no-build /p:PublishAot=true
.\src\WinHarness.Cli\bin\Release\net10.0\win-x64\publish\winharness.exe diagnostics aot
.\src\WinHarness.Cli\bin\Release\net10.0\win-x64\publish\winharness.exe tools list
.\src\WinHarness.Cli\bin\Release\net10.0\win-x64\publish\winharness.exe tools call --name read_file --arguments-json '{"path":"README.md"}'
```

## CLI commands

- `winharness version`
- `winharness diagnostics aot`
- `winharness diagnostics write --message "..."`
- `winharness config init`
- `winharness config wizard` for guided, interactive provider/model setup
- `winharness chat --prompt "..." [--render-markdown true] [--continue-session]` — piped stdin is prepended as a fenced block (`Get-Content README.md | winharness chat --prompt "Summarize"`); attach files with `--files src\Program.cs` (repeatable) or inline `@path` tokens in the prompt
- `winharness chat --prompt "..." --output json` — emit LF-delimited JSONL events (turn/tool/usage/error) on stdout for scripting; see `docs/design/json-events.md`
- `winharness rpc` — long-lived process integration over stdin/stdout JSON (prompt, steer, abort, session ops); see `docs/design/rpc.md` and `samples/rpc-client.ps1`
- `winharness chat` for the terminal REPL (continues the most recent workspace session by default; see [Sessions](#sessions))
- `winharness chat --verbose` to show one persistent line per tool event instead of compact batch summaries
- `winharness chat --tools read_file,grep,glob` to allowlist tools for the run; `--exclude-tools run_command` to deny specific tools; `--no-tools` to disable all tools (applies to built-in and MCP tools; unknown names warn instead of failing)
- `winharness providers list`
- `winharness providers add --id openai-main --base-url https://api.openai.com/v1 [--api-key sk-... --set-default]`
- `winharness providers remove --id openai-main`
- `winharness providers use --provider-id local-ollama`
- `winharness models list --provider-id local-ollama` (add `--filter "local*"` for a case-insensitive wildcard over model ids across all providers)
- `winharness models discover --base-url http://localhost:11434/v1 [--api-key sk-...]` to query the endpoint's `GET /v1/models`
- `winharness models add --provider-id openai-main --id gpt-primary --provider-model-id gpt-4.1 [--tool-calling --vision --set-default]`
- `winharness models set-capabilities --provider-id openai-main --model-id gpt-primary [--tool-calling --vision --reasoning ...]`
- `winharness models use --model-id local-coder`
- `winharness tools list`
- `winharness tools call --name read_file --arguments-json '{"path":"README.md"}'`
- `winharness mcp list`
- `winharness mcp add-stdio --id filesystem --command filesystem-mcp-server.exe [--arguments-json '["--root","C:\\src"]' --environment-json '{}' --enabled true]`
- `winharness mcp add-http --id remote --endpoint https://example.com/mcp [--transport http|sse --headers-json '{"X-Client-Name":"WinHarness"}' --enabled true]`
- `winharness mcp enable --id filesystem`
- `winharness mcp disable --id filesystem`
- `winharness mcp remove --id filesystem`
- `winharness mcp tools`
- `winharness login --provider copilot [--enterprise-domain ghe.example.com]` — GitHub Copilot subscription auth via device code flow (see [Subscription auth](#subscription-auth-oauth))
- `winharness login --provider anthropic` — Claude Pro/Max OAuth (PKCE + loopback; paste fallback)
- `winharness login --provider openai` (alias: `codex`) — ChatGPT Plus/Pro Codex OAuth
- `winharness login status` / `winharness logout --provider copilot`
- `winharness credentials set|get|list|delete`

### Sessions

WinHarness persists multi-turn work as append-only JSONL files with a tree structure (`id` / `parentId`). Each turn stores full artifacts — user messages, assistant text, tool calls, and tool results — not just the final assistant reply.

**Storage path:**

```text
%APPDATA%\WinHarness\sessions\{workspaceKey}\
```

`workspaceKey` is the normalized absolute working directory (for example `c-users-dperez-documents-github-winharness`). Files are named `{yyyyMMdd-HHmmss}_{sessionId}.jsonl`. See `samples/session.example.jsonl` for the on-disk JSON shape.

**`winharness chat` session flags:**

| Flag | Short | Behavior |
| ------ | ------- | ---------- |
| *(default)* | | Continue the most recent session for the current directory, or create a new file if none exist |
| `--no-session` | | Ephemeral in-memory session (v0.1 behavior) |
| `--continue-session` | `-c` | Persist the chat (required for one-shot `--prompt` to write a session file) |
| `--resume` | `-r` | Interactive session picker for this workspace |
| `--session <path\|id>` | | Open a specific file path or match by session id suffix |
| `--name <name>` | `-n` | Set the display name on a newly created session (`session_info` entry) |

The published CLI registers the long option names; use `--continue-session`, `--resume`, and `--name` in scripts.

One-shot `winharness chat --prompt "..."` is **ephemeral by default**. Pass `--continue-session` (or `--session`) to append that turn to a persisted session.

**Slash commands (sessions):**

| Command | Behavior |
| --------- | ---------- |
| `/session` | Show file path, id, display name, leaf id, message count, provider/model |
| `/name <name>` | Append a `session_info` display name |
| `/new` | Create a new session file (previous file remains on disk) |
| `/resume` | In-session picker (same as `--resume`) |
| `/tree` | Navigate session branches (numbered picker; `*` marks the active branch) |
| `/fork` | Copy the active branch into a new session file in the same workspace folder |
| `/clone` | Same copy as `/fork` at the current position (alias with pi-compatible naming) |
| `/export [file]` | Export the active branch to a self-contained HTML page (default) or JSONL (`.jsonl` extension) |
| `/import <file.jsonl>` | Validate a JSONL session file and switch to a new session containing its messages |
| `/compact [instructions]` | Summarize older context; recent messages stay in the active branch |
| `/usage` | Show model, estimated context %, last-turn and session token totals |
| `/trust [always\|never]` | Show or save the project trust decision for this folder |
| `/templates` | List discovered prompt templates |
| `/t <name> [key=value …] [text]` | Expand a prompt template and run it (`{{input}}` receives the trailing text) |

**Automatic compaction** is enabled by default for persisted sessions. Before each turn, WinHarness estimates the active-branch tokens against the model's configured `contextWindow` (fallback 8192 when unset) and compacts proactively when within `reserveTokens` (default 4096) of the limit. When a provider rejects a request for context overflow, WinHarness compacts and retries the turn once. Configure via the `compaction` block in `config.json`:

```json
{
  "compaction": { "autoCompact": true, "reserveTokens": 4096 }
}
```

Set `"autoCompact": false` to rely on manual `/compact` only. Ephemeral sessions (`--no-session`) are never auto-compacted.

**Per-request timeout.** Chat provider requests default to an **infinite** HTTP timeout so high-effort reasoning models can spend arbitrarily long thinking before their first token without WinHarness canceling the request. To bound hangs instead, set `requestTimeoutSeconds` (top-level in `config.json`) to a positive number of seconds:

```json
{
  "requestTimeoutSeconds": 600
}
```

A value of `0` (or omitted) means infinite.

Existing commands still work: `/provider`, `/model`, `/skills`, `/skill`, `/markdown`, `/clear`, `/help`, `/exit`. `/provider` and `/model` append `model_change` entries when the session is persisted.

### Steering (typing while the agent works)

While a turn is running you can keep typing in the REPL:

| Input | Behavior |
| ------- | ---------- |
| `some text` + Enter | **Follow-up**: queued and run as the next prompt after the current turn completes |
| `>> some text` + Enter | **Steering**: delivered to the model after the current tool calls finish, within the same turn |
| `/abort` + Enter | Cancels the turn; unsent steering messages become follow-up input |
| Shift+Enter or Ctrl+J | Insert a newline without submitting (idle prompt and in-turn typing) |

Steering messages are persisted in the session as ordinary user messages. Steering that never finds a tool-round injection point is promoted to a follow-up when the turn ends.

### Editor input

| Input | Behavior |
| ------- | ---------- |
| `"""` | Start multi-line input; finish with `"""` on its own line (text after the opening marker becomes the first line) |
| Shift+Enter / Ctrl+J | Soft newline in the single-line prompt (grows the fixed footer when region mode is on) |
| `!command` | Run via `cmd.exe /c` (captured), print the output, and send it to the model as a user message |
| `!!command` | Run and print only — nothing is sent to the model |
| `@path` | Attach a file: the token stays in the prompt text and the file contents are appended as a fenced block (missing paths are left alone; files over 256 KB are skipped with a notice) |

### Context files

Project instructions are loaded at startup and injected into the system prompt chain (not written to the session file):

| File | Scope |
| ------ | ------- |
| `AGENTS.md` | Global (`%APPDATA%\WinHarness\`), then each ancestor directory down to cwd |
| `CLAUDE.md` | Same walk as `AGENTS.md` (both filenames are supported) |
| `.winharness/SYSTEM.md` | Replaces the built-in runtime system prompt (project first, then global) |
| `.winharness/APPEND_SYSTEM.md` | Appended after the base system prompt (project first, then global) |

The REPL startup banner includes a `context:` line when any of these files are loaded.

### Project trust

Workspaces containing project-local resources (`.winharness\` or `.agents\skills`) require a trust decision before those resources are loaded, since project `SYSTEM.md` and skills can steer the model. Interactive chat prompts once (`always` / `once` / `never`; `always`/`never` persist to `%APPDATA%\WinHarness\trust.json`, and a decision covers all child folders). Non-interactive runs (`--prompt`, `--output json`) never prompt: they use the `defaultProjectTrust` setting (`ask` — default, treated as untrusted — `always`, or `never`), overridable per run with `--approve` / `--no-approve`. Untrusted workspaces still load plain `AGENTS.md`/`CLAUDE.md` context and all global resources. Use `/trust always|never` in the REPL to save a decision.

### Prompt templates

Reusable prompts as Markdown files with optional YAML frontmatter (`name`, `description`) and `{{placeholder}}` slots. Discovered from `.winharness/prompts/` and `.agents/prompts/` in the workspace (trust-gated) and `%APPDATA%\WinHarness\prompts\`:

```markdown
---
name: review
description: Code review with a focus area
---
Review this code for bugs and {{focus}} issues.

{{input}}
```

In the REPL: `/t review focus=security look at the parser`. One-shot: `winharness chat --template review --template-args "focus=security" --prompt "look at the parser"` (`--prompt` fills `{{input}}`). Unfilled placeholders are reported instead of silently sent.

### Skills

WinHarness can load Markdown skill instructions and apply one selected skill to each chat turn. Skills are discovered from:

- `.winharness/skills/**/SKILL.md` under the current workspace
- `.agents/skills/**/SKILL.md` under the current workspace
- `%APPDATA%\WinHarness\skills/**/SKILL.md`
- `%USERPROFILE%\.agents\skills/**/SKILL.md`

A skill can use optional YAML frontmatter:

```markdown
---
name: tdd
description: Test-driven development workflow
---
# TDD

Write a failing test first, then implement the minimum code to pass it.
```

Use `/skills` to list discovered skills, `/skill <name>` to activate one for the session, and `/skill off` to clear it. The selected skill is injected as an additional system message for each turn; it does not rewrite the saved conversation history.

## Configuration

Runtime configuration will live under:

```text
%APPDATA%\WinHarness
```

Set the `WINHARNESS_CONFIG_DIR` environment variable to redirect the entire configuration directory (config, sessions, logs, skills) to another path — useful for smoke tests and CI so runs never touch the real per-user configuration. Note that setting `APPDATA` alone has no effect: the directory is resolved through the Windows known-folder API, not the environment variable.

API keys must be stored in Windows Credential Manager, not configuration files.
WinHarness credential target names must use the `WinHarness:` prefix, for example `WinHarness:openai-main`.

### Subscription auth (OAuth)

`winharness login --provider copilot` signs in with a GitHub Copilot subscription using the OAuth device code flow: visit the printed URL, enter the code, and WinHarness stores the token set in Windows Credential Manager under `WinHarness:oauth:copilot`. The command creates (or updates) a `copilot` provider pointing at your account's Copilot API endpoint; short-lived bearers refresh automatically during chat.

`winharness login --provider anthropic` signs in with Claude Pro/Max via PKCE on a fixed-port loopback (`http://localhost:53692/`). Press Enter while waiting to paste the redirect URL instead (SSH/remote). Tokens are stored under `WinHarness:oauth:anthropic`; the command seeds an `anthropic` provider with `kind: anthropic-messages`.

`winharness login --provider openai` (or `codex`) signs in with ChatGPT Plus/Pro via PKCE on `http://localhost:1455/auth/callback`. Tokens are stored under `WinHarness:oauth:openai`; the command configures an `openai` provider with `kind: openai-codex-responses` (Responses API) and discovers the account's current Codex model catalog. See `docs/adr/ADR-0005-oauth-subscription-providers.md`.

> **Note:** subscription auth rides the unofficial endpoints the vendors ship for their own CLIs. They can change or be revoked at any time (ADR-0005 records this risk acceptance).

Example configuration files are in `samples/`, including `session.example.jsonl` for the persisted session format and a separate `model-capabilities.example.json` shape for model capability metadata.

Create a starter configuration:

```powershell
winharness config init
```

The generated file configures a local OpenAI-compatible Ollama endpoint at `http://localhost:11434/v1`. Edit `%APPDATA%\WinHarness\config.json` to add hosted endpoints.

### Interactive setup

The fastest way to add providers is the guided wizard, which prompts for the
base URL and API key, stores the key in Windows Credential Manager, then queries
the endpoint's `GET /v1/models` route so you can multi-select real model ids
(instead of typing them), assign capabilities, and pick defaults:

```powershell
winharness config wizard
```

You can also list an endpoint's advertised models directly:

```powershell
winharness models discover --base-url https://api.openai.com/v1 --api-key $env:OPENAI_API_KEY
```

### Scripted setup

The same operations are available non-interactively. Adding a provider with
`--api-key` stores the secret in Windows Credential Manager under
`WinHarness:<id>` automatically:

```powershell
winharness providers add --id openai-main --base-url https://api.openai.com/v1 --api-key $env:OPENAI_API_KEY --set-default
winharness models add --provider-id openai-main --id gpt-primary --provider-model-id gpt-4.1 --tool-calling --vision --set-default
```

Keyless local endpoints (Ollama, LM Studio, vLLM) simply omit `--api-key`:

```powershell
winharness providers add --id local-ollama --base-url http://localhost:11434/v1 --set-default
winharness models add --provider-id local-ollama --id local-coder --provider-model-id qwen2.5-coder:latest
```

## Provider setup

OpenAI-compatible providers use the official `OpenAI` SDK plus `Microsoft.Extensions.AI.OpenAI` for the `IChatClient` adapter. Each configured model declares its own capabilities instead of relying on provider-wide hardcoding.

Example hosted provider:

```json
{
  "id": "openai-main",
  "kind": "openai-compatible",
  "baseUrl": "https://api.openai.com/v1",
  "credentialName": "WinHarness:openai-main",
  "models": [
    {
      "id": "gpt-primary",
      "providerModelId": "gpt-4.1",
      "capabilities": {
        "streaming": true,
        "toolCalling": true,
        "vision": true,
        "promptCaching": false,
        "structuredOutput": true,
        "reasoning": true
      }
    }
  ]
}
```

Store the API key:

```powershell
winharness credentials set --target-name WinHarness:openai-main --secret $env:OPENAI_API_KEY
```

Select defaults:

```powershell
winharness providers use --provider-id openai-main
winharness models use --model-id gpt-primary
```

## MCP setup

MCP servers can use stdio, Streamable HTTP, or HTTP with SSE. WinHarness discovers tools with explicit client-side `tools/list` and calls them with `tools/call`; it does not use assembly scanning or attribute-based discovery in the AOT path.

Stdio example:

```powershell
winharness mcp add-stdio --id filesystem --command filesystem-mcp-server.exe --arguments-json '["--root","C:\\src"]' --enabled true
```

Streamable HTTP example:

```powershell
winharness mcp add-http --id remote --endpoint https://example.com/mcp --transport http --enabled true
```

HTTP with SSE example:

```powershell
winharness mcp add-http --id legacy-sse --endpoint https://example.com/sse --transport sse --enabled true
```

Non-secret headers are supplied as JSON when needed:

```powershell
winharness mcp add-http --id remote --endpoint https://example.com/mcp --headers-json '{"X-Client-Name":"WinHarness"}'
```

Inspect MCP tools:

```powershell
winharness mcp list
winharness mcp tools
```

## Built-in tools

Built-in tools are exposed through the same `ITool` registry as MCP tools:

- `read_file`
- `write_file`
- `edit_file`
- `run_command`
- `glob`
- `grep`

`run_command` defaults to captured stdout/stderr for clean agent-readable output. Set `mode` to `interactive` only when a command needs a real terminal; that path uses the Windows ConPTY executor. Captured mode is non-interactive; to feed data to a command that reads stdin, use the `input` field (UTF-8, no BOM). The field writes the text and then closes stdin so the process exits cleanly.

Example:

```powershell
winharness tools call --name run_command --arguments-json '{"command":"dotnet","arguments":["--info"],"timeoutSeconds":30}'
```

Stdin input example:

```powershell
winharness tools call --name run_command --arguments-json '{"command":"cmd.exe","arguments":["/c","findstr .*"],"input":"hello\\n","timeoutSeconds":10}'
```

## Diagnostics

WinHarness writes structured local JSONL diagnostics under:

```text
%APPDATA%\WinHarness\logs
```

Diagnostics include provider calls, tool execution timing, failures, and selected command execution mode. No external telemetry is emitted.

## Architectural decisions

ADRs are stored in `docs/adr/`.
