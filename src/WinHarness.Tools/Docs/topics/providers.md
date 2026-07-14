# WinHarness providers and models

## Provider kinds

- `openai-compatible` — OpenAI-style `/v1` chat completions (Ollama, OpenRouter, gateways, …)
- `anthropic-messages` — Anthropic Messages API
- `openai-codex-responses` — ChatGPT/Codex subscription (OAuth)

## Config

Providers and models live in `%APPDATA%\WinHarness\config.json` under `providers[]`.

Each model entry has `id` (WinHarness alias) and `providerModelId` (sent to the API). Capabilities (`toolCalling`, `reasoning`, `vision`, …) affect runtime behavior.

## Login (subscription)

```text
winharness login --provider openai
winharness login --provider anthropic
```

OAuth tokens are refreshed per request; stored in Credential Manager.

## CLI

```text
winharness providers list
winharness models list --provider-id <id>
winharness models use --model-id <id>
winharness config wizard
```

## Chat slash commands

`/providers`, `/provider <id>`, `/models [provider]`, `/model [id]`, `/effort [level]`

Reasoning effort: `none`, `low`, `medium`, `high`, `extra-high`. Codex sends `reasoning.effort` to the API; high effort adds latency before visible output.

## Request timeout

`requestTimeoutSeconds` in `config.json` — `0` or omitted means infinite (default, for long reasoning). Set a positive value to bound hung requests.
