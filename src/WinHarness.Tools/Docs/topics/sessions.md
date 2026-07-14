# WinHarness sessions

Sessions are append-only JSONL files with a tree of entries (`id` / `parentId`). Each turn stores full artifacts: user messages, assistant text, tool calls, and tool results.

## Storage

```text
%APPDATA%\WinHarness\sessions\{workspaceKey}\
```

Files: `{yyyyMMdd-HHmmss}_{sessionId}.jsonl`

## Count or list sessions (PowerShell)

```powershell
pwsh -NoProfile -Command "(Get-ChildItem -Path (Join-Path $env:APPDATA 'WinHarness\sessions') -Recurse -Filter *.jsonl).Count"
```

Add `-File` to list paths; filter by workspace subfolder name when needed.

## Chat flags

| Flag | Behavior |
| --- | --- |
| *(default)* | Continue the most recent session for cwd, or create a new file |
| `--no-session` | Ephemeral in-memory session |
| `--continue-session` / `-c` | Persist (required for one-shot `--prompt` to write a file) |
| `--resume` / `-r` | Interactive session picker |
| `--session <path\|id>` | Open a specific file or id suffix |
| `--name <name>` / `-n` | Display name on a new session |

One-shot `winharness chat --prompt` is ephemeral unless `--continue-session` or `--session` is passed.

## Session slash commands

`/session`, `/name`, `/new`, `/resume`, `/delete`, `/tree`, `/fork`, `/clone`, `/export`, `/import`, `/compact`, `/usage`

## Compaction

Configured in `config.json` → `compaction.autoCompact` (default true) and `compaction.reserveTokens` (default 4096). Ephemeral sessions are never auto-compacted.

## Prune CLI

`winharness sessions prune` removes old session files (see `winharness sessions prune --help`).
