# WinHarness built-in tools

Built-in tools are scoped to the **workspace root** (chat cwd). Paths outside the workspace require `run_command`.

## Tools

| Name | Purpose |
| --- | --- |
| `read_file` | Read UTF-8 text |
| `write_file` | Create/overwrite file |
| `edit_file` | Search/replace edit |
| `run_command` | Run a process (Windows/PowerShell) |
| `glob` | Match files by simple wildcard pattern |
| `grep` | Search file contents |
| `winharness_docs` | Embedded WinHarness reference (this catalog) |

MCP tools are exposed as `{serverId}.{toolName}` when configured.

## run_command rules

- `command` must be the executable only (`pwsh`, `cmd.exe`, `dotnet`, `where.exe`, …).
- Put flags in the `arguments` array.
- Prefer `pwsh -NoProfile -Command` or `cmd.exe /c` for shell syntax.
- Default timeout: 60 seconds (`timeoutSeconds` to override).
- Do not use Unix builtins (`ls`, `cat`, `which`, `bash`) unless you know they exist on this host.

## glob / grep skipped directories

`.git`, `.claude`, `bin`, `obj`, `node_modules`, `.vs`, `.idea`, `.vscode`

`.claude/worktrees` duplicates are intentionally skipped — they are a common source of very slow searches.

## CLI equivalents

```text
winharness tools list
winharness tools call --name <name> --arguments-json '{...}'
winharness docs list
winharness docs get --topic sessions
```

## Tool filters (chat)

`--tools read_file,grep` allowlist; `--exclude-tools run_command` denylist; `--no-tools` disables all tools for the run.
