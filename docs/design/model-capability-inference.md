# Model capability inference from `/models` discovery

## Status

Proposed — work item, not yet implemented.

## Problem

When WinHarness auto-discovers models from an OpenAI-compatible provider's
`GET /v1/models` endpoint, it should infer as many `ProviderCapabilities` as
possible from the discovery payload instead of falling back to brittle
name-substring heuristics or forcing the user to toggle six checkboxes by hand.

Today inference works for two of six capabilities when the endpoint exposes the
non-standard `architecture.modality` / `reasoning.supported_efforts` extensions
(Venice and OpenRouter do; bare OpenAI does not), and for the other four it
relies on substring matches against the model id (`gpt-4o`, `claude-3`,
`gemini-2.0`, `deepseek`). Venice's model ids don't contain any of those
substrings, so `PromptCaching` and `StructuredOutput` come back false for every
Venice model even when the model supports them.

A second, independent gap: the inference code only runs from the interactive
`config wizard`. The non-interactive `winharness models discover` command calls
`IModelCatalog.ListModelsAsync` directly and prints `id\towned_by` — it does no
capability inference at all and never persists anything.

## Current state (as of writing)

### Discovery layer — `OpenAiCompatibleModelCatalog`

`src/WinHarness.Providers/OpenAiCompatibleModelCatalog.cs` parses each
`/models` entry into a `CatalogModel(Id, OwnedBy, ContextWindow, Vision,
SupportedReasoningEfforts)` reading:

- `id`, `owned_by` — standard OpenAI.
- `context_length` **or** `context_window` — non-standard, picked up via
  `model.ContextWindow ?? model.ContextLength`.
- `architecture.modality` — non-standard. `Vision` becomes true when the
  string contains `image` or `multimodal` (case-insensitive).
- `reasoning.supported_efforts` — non-standard. Carried through verbatim as
  `List<string>?`.

Everything else in the response is discarded. The Venice `/models` payload
actually exposes more than this (see "Venice-specific fields" below) but the
parser doesn't read it.

### Inference layer — `ProviderWizard.InferCapabilities`

`src/WinHarness.Cli/Configuration/ProviderWizard.cs` owns a `private static`
`InferCapabilities(string modelId, bool? endpointVision, List<string>?
endpointReasoningEfforts)` that produces the full `ProviderCapabilities`:

| Capability        | Source                                                                  |
| ----------------- | ----------------------------------------------------------------------- |
| `Vision`          | endpoint `architecture.modality` only — never guessed by name           |
| `Reasoning`       | endpoint `reasoning.supported_efforts` non-empty — never guessed        |
| `Streaming`       | true for chat models; false for `embedding`/`moderation`/`whisper`/`tts`/`dall-e`/`rerank` |
| `ToolCalling`     | same non-chat guard as `Streaming`                                      |
| `PromptCaching`   | name substring: `claude-3`, `gemini-1.5`, `gemini-2.0`, `deepseek`      |
| `StructuredOutput`| name substring: `gpt-4o`, `gpt-4-turbo`, `gemini-1.5`, `gemini-2.0`      |

The wizard then shows a multi-select with these as pre-ticked defaults and
persists whatever the user confirms via `ProviderConfigurator.AddModelAsync`.

### Runtime layer — `ConfigurationModelCapabilityRegistry`

`src/WinHarness.Providers/ConfigurationModelCapabilityRegistry.cs` just reads
the persisted `ModelOptions.Capabilities` back. It does **not** re-query the
endpoint. So whatever was inferred at config time is frozen into `config.json`.

### Non-interactive `models discover`

`src/WinHarness.Cli/Program.cs` line 149 — calls
`IModelCatalog.ListModelsAsync` and prints `id` (+ `owned_by` if present). No
inference, no persistence. This is the path a scriptable setup would use.

## Goals

1. Infer `ToolCalling`, `StructuredOutput`, and `PromptCaching` from the
   discovery payload when the endpoint advertises them, instead of from model
   id substrings — at minimum for Venice, ideally for OpenRouter too.
2. Run the same inference from `winharness models discover` (and any other
   non-interactive caller), not just from `config wizard`.
3. Keep the existing name heuristics as a fallback for endpoints (e.g. bare
   OpenAI) that don't expose the richer fields.
4. Stay Native AOT safe — source-generated `JsonSerializerContext`, no
   reflection.

## Non-goals

- Re-querying the endpoint at runtime. Capabilities stay config-time-only and
  persisted to `config.json`. `ConfigurationModelCapabilityRegistry` is
  unchanged.
- Adding new `ProviderCapabilities` fields. The six existing booleans are the
  contract; if a Venice field doesn't map onto one of them, leave it for now.
- Inference from model id for `Vision`/`Reasoning`. The current "don't guess
  by common knowledge" stance for those two is intentional and should stay —
  endpoint hints only.

## Venice-specific fields to map

The Venice `/api/v1/models` response (OpenRouter-style) carries, per model,
fields beyond what the parser currently reads. The exact shapes to confirm
against a live response (run `winharness models discover --base-url
https://api.venice.ai/api/v1 --api-key $env:VENICE_API_KEY` and inspect), but
the likely candidates are:

- `architecture.modality` — already read; keep using for `Vision`.
- `architecture.input_modalities` / `architecture.output_modalities` —
  array form of the above; safer to check than the single `modality` string
  when present.
- `reasoning.supported_efforts` — already read; keep using for `Reasoning`.
- `capabilities` or similar object — may advertise `tool_calls`,
  `structured_output`, `parallel_tool_calls`, `vision`, etc. **Confirm the
  exact key names against a live response before wiring them up.**
- `top_provider.context_length` — alternate location for context window; the
  parser already accepts `context_window` / `context_length`, add this as a
  third fallback.
- `description` / `name` — human-readable; not used for inference but could
  improve the wizard UI later.

> The first concrete step is capturing a real Venice `/models` response to a
> fixture file and committing it under
> `tests/WinHarness.IntegrationTests/Data/` so the field names are pinned.

## Proposed design

### Step 1 — Capture a Venice fixture and extend the parser

Add `tests/WinHarness.IntegrationTests/Data/venice-models.json` (redacted,
single entry minimum, multiple preferred to cover vision/non-vision and
reasoning/non-reasoning). Then extend `ModelListEntry` in
`OpenAiCompatibleModelCatalog.cs` with the additional
`[JsonPropertyName]` properties needed to read the richer fields, and extend
`CatalogModel` (in `src/WinHarness.Abstractions/Providers/IModelCatalog.cs`)
to carry them through.

Concretely, `CatalogModel` would gain nullable, source-generation-safe
booleans/strings for whatever Venice exposes that maps onto a capability,
e.g.:

```csharp
public sealed record CatalogModel(
    string Id,
    string? OwnedBy,
    int? ContextWindow = null,
    bool? Vision = null,
    List<string>? SupportedReasoningEfforts = null,
    bool? ToolCalling = null,        // new — endpoint-advertised
    bool? StructuredOutput = null,   // new
    bool? PromptCaching = null);     // new
```

The parser maps `null` to "endpoint didn't say" so the inference layer can
fall back to name heuristics only when the endpoint is silent. A present
`false` is respected as "endpoint says no."

Add a `ModelListEntry` test that parses the Venice fixture and asserts each
new field round-trips. Extend `OpenAiCompatibleModelCatalogTests` with a
Venice-shape test alongside the existing bare-OpenAI-shape tests.

### Step 2 — Extract inference into a service

Move `InferCapabilities` out of `ProviderWizard` (which has a
`Spectre.Console` dependency) into a new type in
`src/WinHarness.Providers/` with no UI dependencies:

```csharp
namespace WinHarness.Providers;

/// <summary>
/// Infers ProviderCapabilities from a discovered CatalogModel, falling back
/// to model-id heuristics only when the endpoint did not advertise a field.
/// </summary>
public interface IModelCapabilityInferrer
{
    ProviderCapabilities Infer(CatalogModel model);
}
```

Default implementation keeps the existing name heuristics for
`PromptCaching`/`StructuredOutput`/`Streaming`/`ToolCalling` but prefers the
endpoint-supplied value when non-null. `Vision`/`Reasoning` remain
endpoint-only (false when endpoint is silent), preserving current behavior.

Register it in `ProviderServiceCollectionExtensions`.

`ProviderWizard` takes `IModelCapabilityInferrer` instead of calling its own
`private static`. `PromptCapabilities` stays in the wizard (it's the UI layer)
and is seeded from `inferrer.Infer(model)`.

### Step 3 — Wire inference into `models discover`

`winharness models discover` currently just prints ids. Two options:

- **A. Print inferred capabilities alongside ids** — minimal, no persistence.
  Output becomes `id\towned_by\tvision,reasoning,...` so a user can see what
  would be inferred without running the wizard.
- **B. Add a `--persist --provider-id <id>` flag** that loops the discovered
  models through `IModelCapabilityInferrer` and calls
  `ProviderConfigurator.AddModelAsync` for each (with auto-derived aliases
  reusing `ProviderWizard.DeriveAlias`, which should also be lifted into the
  inferrer or a small shared helper).

Recommend doing A first (low risk, unblocks scripting visibility) and B as a
follow-up. B should reuse the wizard's alias-derivation and
"apply shared capabilities vs per-model" logic, so lift `DeriveAlias` into a
shared spot at the same time.

### Step 4 — Keep the name heuristics as fallback

The substring table (`claude-3`, `gpt-4o`, `gemini-2.0`, `deepseek`, etc.) is
still useful for bare OpenAI, Azure OpenAI, and any endpoint that returns the
minimal shape. Keep it, but make sure the precedence is:

1. Endpoint-advertised value (non-null) — authoritative.
2. Name heuristic — only when endpoint is silent.
3. Conservative default (false) — when neither applies, except `Streaming`
   and `ToolCalling` which stay true for non-`embedding`/`moderation`/etc. ids.

Document the precedence in the inferrer's XML doc comments.

## Files to touch

- `src/WinHarness.Abstractions/Providers/IModelCatalog.cs` — extend
  `CatalogModel` record.
- `src/WinHarness.Providers/OpenAiCompatibleModelCatalog.cs` — extend
  `ModelListEntry` / `ModelArchitecture` / `ModelReasoning` (or add sibling
  types) with the extra `[JsonPropertyName]` fields; map them onto
  `CatalogModel`. Keep `ModelCatalogJsonContext` source-generated.
- `src/WinHarness.Providers/IModelCapabilityInferrer.cs` (new) — interface +
  default implementation, lifted from `ProviderWizard.InferCapabilities`.
- `src/WinHarness.Providers/ProviderServiceCollectionExtensions.cs` — register
  the inferrer.
- `src/WinHarness.Cli/Configuration/ProviderWizard.cs` — inject
  `IModelCapabilityInferrer`, delete the `private static InferCapabilities`,
  optionally lift `DeriveAlias` to a shared helper.
- `src/WinHarness.Cli/Program.cs` — `models discover` path (lines ~149-160):
  print inferred capabilities (step 3A), optionally add `--persist` (3B).
- `tests/WinHarness.IntegrationTests/Data/venice-models.json` (new fixture).
- `tests/WinHarness.IntegrationTests/OpenAiCompatibleModelCatalogTests.cs` —
  Venice-shape parse test.
- `tests/WinHarness.UnitTests/ModelCapabilityInferrerTests.cs` (new) — covers
  endpoint-wins-over-name, name-fallback-when-endpoint-silent,
  vision/reasoning-endpoint-only, non-chat-id guard.

## Risks / open questions

- **Field names must be confirmed against a live Venice response.** This doc
  deliberately avoids hard-coding them; the fixture in step 1 is the source of
  truth. Don't guess and don't copy field names from OpenRouter docs without
  verifying Venice matches.
- **`false` vs absent.** The parser needs to distinguish "endpoint says false"
  from "endpoint didn't say." Using nullable bools on `CatalogModel` and
  `ModelListEntry` handles this; make sure the inference layer treats `null`
  as "use fallback" and a present `false` as authoritative-false.
- **Backward compat for existing `config.json`.** `ModelOptions.Capabilities`
  is unchanged; existing configs keep working. Newly discovered models will
  just get better defaults.
- **OpenRouter parity.** The same parser changes likely improve OpenRouter
  inference too since the schemas overlap, but don't claim OpenRouter support
  without a fixture.
- **AOT.** Any new `[JsonPropertyName]` types must be added to
  `ModelCatalogJsonContext`'s `[JsonSerializable]` list. No `JsonSerializer`
  reflection calls.
- **Non-chat id guard.** The `embedding`/`moderation`/`whisper`/`tts`/
  `dall-e`/`rerank` substring guard in `InferCapabilities` should move with
  the inferrer and stay name-based (the endpoint rarely flags these
  separately).
