# WinHarness

WinHarness is a Windows-native, terminal-first AI coding harness designed around Native AOT, MCP stdio tools, and OpenAI-compatible model endpoints.

## v0.1 scope

WinHarness v0.1 focuses on OpenAI-compatible endpoints only:

- OpenAI
- Ollama `/v1`
- LM Studio `/v1`
- vLLM `/v1`
- OpenRouter-compatible endpoints
- other compatible `/v1` chat-completions style APIs

Anthropic-native support is deferred until its official .NET SDK path can satisfy the Native AOT gate or an ADR approves another AOT-safe approach.

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
- `winharness chat --prompt "..." [--render-markdown true]`
- `winharness chat` for the terminal REPL (`/help`, `/providers`, `/models`, `/provider <id>`, `/model <id>`, `/markdown`, `/new`, `/exit`)
- `winharness providers list`
- `winharness providers add --id openai-main --base-url https://api.openai.com/v1 [--api-key sk-... --set-default]`
- `winharness providers remove --id openai-main`
- `winharness providers use --provider-id local-ollama`
- `winharness models list --provider-id local-ollama`
- `winharness models discover --base-url http://localhost:11434/v1 [--api-key sk-...]` to query the endpoint's `GET /v1/models`
- `winharness models add --provider-id openai-main --id gpt-primary --provider-model-id gpt-4.1 [--tool-calling --vision --set-default]`
- `winharness models use --model-id local-coder`
- `winharness tools list`
- `winharness tools call --name read_file --arguments-json '{"path":"README.md"}'`
- `winharness mcp list`
- `winharness mcp tools`
- `winharness credentials set|get|list|delete`

## Configuration

Runtime configuration will live under:

```text
%APPDATA%\WinHarness
```

API keys must be stored in Windows Credential Manager, not configuration files.
WinHarness credential target names must use the `WinHarness:` prefix, for example `WinHarness:openai-main`.

Example configuration files are in `samples/`, including a separate `model-capabilities.example.json` shape for model capability metadata.

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

MCP servers are configured as stdio processes. WinHarness discovers tools with explicit client-side `tools/list` and calls them with `tools/call`; it does not use assembly scanning or attribute-based discovery in the AOT path.

Example:

```json
{
  "id": "filesystem",
  "command": "filesystem-mcp-server.exe",
  "arguments": ["--root", "C:\\src"],
  "workingDirectory": null,
  "environment": {},
  "enabled": true,
  "startupTimeoutSeconds": 30
}
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

`run_command` defaults to captured stdout/stderr for clean agent-readable output. Set `mode` to `interactive` only when a command needs a real terminal; that path uses the Windows ConPTY executor.

Example:

```powershell
winharness tools call --name run_command --arguments-json '{"command":"dotnet","arguments":["--info"],"timeoutSeconds":30}'
```

## Diagnostics

WinHarness writes structured local JSONL diagnostics under:

```text
%APPDATA%\WinHarness\logs
```

Diagnostics include provider calls, tool execution timing, failures, and selected command execution mode. No external telemetry is emitted.

## Architectural decisions

ADRs are stored in `docs/adr/`.
