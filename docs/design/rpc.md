# RPC Mode (`winharness rpc`)

Long-lived process integration: LF-delimited single-line JSON requests on
**stdin**, responses and turn events on **stdout**. Split records on `\n`
only — payload strings escape newlines inside JSON, so byte-level LF always
terminates a record. Diagnostics go to stderr.

One session at a time; prompts run serially. RPC never shows a trust prompt:
project-local resources load only via a saved trust.json decision or
`defaultProjectTrust: "always"`.

## Requests

```json
{"id":"<correlation id>","method":"<method>", ...fields}
```

| Method | Fields | Behavior |
|--------|--------|----------|
| `prompt` | `prompt`, optional `providerId`/`modelId`/`session`/`name` on first call | Runs a turn; opens a session on first use (continues the workspace's most recent, or `session` by path/id suffix) |
| `steer` | `text` | Queues a steering message for the running turn (delivered between tool round-trips) |
| `abort` | — | Cancels the running turn |
| `new_session` | optional `providerId`/`modelId`/`session`/`name` | Opens a fresh session |
| `get_session` | — | Returns session id/file/provider/model |
| `set_model` | `providerId` and/or `modelId` | Switches the open session's target (rejected mid-turn) |
| `shutdown` | — | Acknowledges and exits |

## Responses and events

Every request gets exactly one response record:

```json
{"kind":"response","id":"<request id>","ok":true}
{"kind":"response","id":"<request id>","ok":false,"error":"..."}
{"kind":"response","id":"<request id>","ok":true,"result":{"sessionId":"...","file":"...","providerId":"...","modelId":"..."}}
```

A `prompt` additionally streams events (the same shapes as
`chat --output json`, see [json-events.md](json-events.md)) wrapped with the
initiating request id:

```json
{"kind":"event","requestId":"p1","event":{"type":"turn_start","providerId":"local","modelId":"coder"}}
{"kind":"event","requestId":"p1","event":{"type":"assistant_delta","text":"Hello"}}
{"kind":"event","requestId":"p1","event":{"type":"turn_end"}}
```

The terminal event for a turn is `turn_end` (success) or `error` (failure,
including aborts). The `prompt` response arrives before the events; a second
`prompt` while a turn runs is rejected — use `steer` or `abort`.

## Stability

Same additive-only rule as the JSON event stream: new methods, fields, and
event types may appear in minor releases; existing names are stable.

A reference client is at `samples/rpc-client.ps1`.
