# WinHarness paths

WinHarness data lives outside the workspace. Do not use glob/grep in the project tree for these.

## Configuration root

- Default: `%APPDATA%\WinHarness`
- Override for tests/CI: set environment variable `WINHARNESS_CONFIG_DIR` to an absolute directory (config, sessions, logs, and skills all move with it).
- Main config file: `config.json`
- Model capability overrides: `model-capabilities.json`
- Project trust decisions: `trust.json`
- Global context: `AGENTS.md`, `SYSTEM.md`, `APPEND_SYSTEM.md`
- Global skills: `skills\` (and synced copies under this tree)
- Global prompt templates: `prompts\`

## Sessions

- `%APPDATA%\WinHarness\sessions\{workspaceKey}\{yyyyMMdd-HHmmss}_{sessionId}.jsonl`
- `workspaceKey` is the normalized absolute cwd (for example `c-users-dperez-documents-github-winharness`).
- Count or list session files with PowerShell against this directory, not workspace search tools.

## Logs

- `%APPDATA%\WinHarness\logs\winharness.jsonl` — provider and tool timing events.

## Credentials

- API keys and OAuth token sets are stored via Windows Credential Manager (`credentialName` in config), not in `config.json`.
