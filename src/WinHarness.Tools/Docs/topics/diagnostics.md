# WinHarness diagnostics

## Log file

`%APPDATA%\WinHarness\logs\winharness.jsonl`

Each line is a JSON object with `timestamp`, `category`, `eventName`, `message`, and `properties`.

## Categories

| Category | Events | Key properties |
| --- | --- | --- |
| `provider` | `provider.started`, `provider.completed`, `provider.failed` | `provider.id`, `model.id`, `provider.duration_ms`, `usage.*_tokens` |
| `tool` | `tool.completed`, `tool.failed` | `tool.name`, `tool.duration_ms`, `tool.succeeded` |

Provider `duration_ms` on `provider.completed` is **wall-clock for the entire turn** (all tool rounds included), not a single HTTP call.

Tool `duration_ms` is per tool invocation.

## CLI

```text
winharness diagnostics aot
winharness diagnostics write --message "..."
```

## Debugging slow turns

1. Find the turn window in `winharness.jsonl`.
2. Check which `tool.completed` entry has the largest `tool.duration_ms` (often `grep` on large trees).
3. Compare `provider.duration_ms` across providers/models.
4. Use `winharness chat --verbose` to see one line per tool with timings in the REPL.
