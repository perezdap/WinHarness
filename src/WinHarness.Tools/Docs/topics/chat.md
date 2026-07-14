# WinHarness chat REPL

## Launch

```text
winharness chat
winharness chat --verbose
winharness chat --output json
winharness rpc
```

Default: continues the most recent persisted session for the current directory.

## Common slash commands

| Command | Purpose |
| --- | --- |
| `/help` | Command list |
| `/session` | Session file path, id, name, counts |
| `/new` | New session file |
| `/resume` | Pick a saved session |
| `/model` / `/provider` | Switch model or provider |
| `/effort` | Reasoning effort |
| `/skills` / `/skill` | Session skills |
| `/compact` | Summarize older context |
| `/clear` | Clear in-memory view (file unchanged) |
| `/exit` | Leave |

See `winharness_docs` topic `sessions` for session-specific commands.

## While a turn is running

| Input | Behavior |
| --- | --- |
| text + Enter | Follow-up queued for after the turn |
| `>> text` + Enter | Steering injected between tool rounds |
| `/abort` | Cancel turn |

## Context files (project)

Loaded from workspace: `AGENTS.md`, `CLAUDE.md`, `.winharness/SYSTEM.md`, skills under `.agents/skills` or `.winharness/skills` when trusted.

Global copies under `%APPDATA%\WinHarness\`.

## WinHarness self-reference

Use `winharness_docs` (or `winharness docs get --topic <id>`) for paths, sessions, tools, and diagnostics — not workspace grep.
