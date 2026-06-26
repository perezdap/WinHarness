# ADR-0002: Wrap Microsoft.Extensions.AI.IChatClient

## Status

Accepted

## Context

WinHarness needs a provider abstraction that can support streaming, model capabilities, tool calling, diagnostics, and future provider expansion without binding the runtime to a single SDK.

Both the .NET ecosystem and the MCP C# SDK increasingly use `Microsoft.Extensions.AI` abstractions, especially `IChatClient`, `ChatOptions`, and `AIFunction`.

## Decision

`IChatProvider` wraps and exposes `Microsoft.Extensions.AI.IChatClient` instead of replacing it with a bespoke chat abstraction.

For OpenAI-compatible endpoints, WinHarness uses:

- `OpenAI` as the official OpenAI SDK.
- `Microsoft.Extensions.AI.OpenAI` as the adapter layer that exposes the official SDK through `IChatClient`.

## Rationale

Using `IChatClient` preserves the standard .NET AI abstraction for:

- streaming response APIs,
- tool/function invocation plumbing,
- middleware composition,
- logging and telemetry hooks,
- MCP tool compatibility.

WinHarness-specific provider objects are still useful for configuration, credential resolution, model capability metadata, and diagnostics, but they should compose around `IChatClient` rather than reinventing the transport and message model.

## Consequences

- Runtime code works with provider-neutral `IChatClient` instances.
- Provider capabilities live in WinHarness metadata, not in provider-specific `switch` statements.
- Tool adapters can target `AIFunction` explicitly.
- The OpenAI-compatible provider requires both `OpenAI` and `Microsoft.Extensions.AI.OpenAI`.
