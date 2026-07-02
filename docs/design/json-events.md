# JSON Event Stream (`chat --output json`)

`winharness chat --prompt "..." --output json` emits LF-delimited JSONL events
on **stdout**. All human/diagnostic output goes to **stderr**. Consumers must
split records on `\n` only (payload strings escape newlines as `\n` inside
JSON, so byte-level LF always terminates a record).

Requires `--prompt` (one-shot mode). The process exits nonzero when the turn
ends in an `error` event.

## Events

Fields are omitted when null. `type` is the stable discriminator.

| `type` | Fields | Meaning |
|--------|--------|---------|
| `turn_start` | `providerId`, `modelId` | Turn began |
| `assistant_delta` | `text` | Streaming assistant text fragment |
| `tool` | `toolName`, `phase` (`started`\|`completed`\|`failed`), `succeeded?`, `durationMs?` | Tool call phase change |
| `assistant_message` | `text` | Final assistant text for the turn |
| `usage` | `inputTokens?`, `outputTokens?` | Token usage when the provider reported it |
| `turn_end` | — | Terminal success event |
| `error` | `error` | Terminal failure event (exit code 1) |

## Example

```text
{"type":"turn_start","providerId":"local-ollama","modelId":"local-coder"}
{"type":"assistant_delta","text":"Hello"}
{"type":"tool","toolName":"read_file","phase":"started"}
{"type":"tool","toolName":"read_file","phase":"completed","succeeded":true,"durationMs":12}
{"type":"assistant_message","text":"Hello, the file says..."}
{"type":"usage","inputTokens":812,"outputTokens":96}
{"type":"turn_end"}
```

## Stability

Additive changes only: new event types and new optional fields may appear in
minor releases. Existing `type` values and field names will not be renamed or
removed without a major version bump.
