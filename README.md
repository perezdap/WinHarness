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

## Configuration

Runtime configuration will live under:

```text
%APPDATA%\WinHarness
```

API keys must be stored in Windows Credential Manager, not configuration files.

Example configuration files are in `samples/`.

## Architectural decisions

ADRs are stored in `docs/adr/`.
