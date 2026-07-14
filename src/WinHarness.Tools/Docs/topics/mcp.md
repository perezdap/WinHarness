# WinHarness MCP

MCP servers extend the tool list beyond built-ins. Each enabled server contributes tools named `{serverId}.{toolName}`.

## Config location

`%APPDATA%\WinHarness\config.json` → `mcpServers[]`

Each entry: `id`, `transport` (`stdio` or `http`/`sse`), `enabled`, `startupTimeoutSeconds`, plus transport-specific fields.

## CLI

```text
winharness mcp list
winharness mcp add-stdio --id filesystem --command <exe> [--arguments-json '[...]']
winharness mcp add-http --id remote --endpoint <url> [--transport http|sse]
```

## Runtime behavior

- Tools are discovered per turn via `ListToolsAsync` on each enabled server.
- A single failing server is skipped; other servers and built-ins still load.
- Stdio servers spawn on first use; startup is bounded by `startupTimeoutSeconds`.

## When to use MCP vs built-ins

Use built-in `read_file` / `grep` / `run_command` for workspace-local work. Use MCP for domain-specific integrations (browser, issue trackers, custom services).
