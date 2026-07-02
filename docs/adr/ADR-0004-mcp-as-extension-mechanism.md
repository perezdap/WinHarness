# ADR-0004: Use MCP, skills, and prompt templates as the extension mechanism

## Status

Accepted

## Context

Pi, the closest comparable harness, builds its identity around a dynamic TypeScript extension system: extensions register custom tools, commands, event handlers, and UI components, loaded at runtime from user or project directories and distributed as packages. A feature-gap analysis against pi identified extensibility as the largest structural difference.

WinHarness cannot copy that design:

- Native AOT is the hardest project constraint (ADR-0001, ADR-0003). Runtime code loading — `Assembly.LoadFrom`, Roslyn scripting, `AssemblyLoadContext` plugins — is unavailable or unsupported in a Native AOT executable.
- Embedding an interpreter (Lua, JavaScript, WASM) would add a large dependency that must itself pass the zero-warning AOT gate, and would create a second programming model alongside the existing tool abstraction.
- WinHarness already ships an extension surface pi deliberately omits: MCP client support over stdio, Streamable HTTP, and SSE, exposed through the same `ITool` registry as built-in tools.

The user explicitly approved relying on MCP rather than building an extension system.

## Decision

WinHarness will not ship a dynamic extension or plugin system. Extensibility is provided by three existing or planned mechanisms:

| Need | Mechanism |
|------|-----------|
| Custom tools and capabilities | MCP servers (stdio, Streamable HTTP, or SSE), in any language |
| Custom instructions and workflows | Skills (`SKILL.md` discovery) and context files (`AGENTS.md`, `SYSTEM.md`, `APPEND_SYSTEM.md`) |
| Reusable parameterized prompts | Prompt templates (planned, `docs/design/pi-parity-roadmap.md` PR-C5) |

Programmatic integration (driving WinHarness from another process) is served by the planned JSON event stream and RPC mode (roadmap PR-C2/PR-C3), not by in-process plugins.

## Rationale

- **AOT-compatible by construction.** MCP servers are separate processes; nothing is loaded into the WinHarness binary. The AOT gate stays intact.
- **Language-agnostic.** A pi extension must be TypeScript. A WinHarness capability can be written in anything that speaks MCP, including existing off-the-shelf servers.
- **One tool model.** MCP tools and built-in tools already flow through the same `ITool` registry, tool-gating policy, and diagnostics. An extension system would introduce a second, privileged path.
- **Deliberate inversion of pi's stance.** Pi says "no MCP, build extensions instead" because its in-process extension API can subsume MCP. WinHarness inverts this: process isolation is the feature, not the limitation, for a security-conscious Windows-native binary.

## Consequences

- UX-level hooks are not user-extensible in v0.x: custom status lines, permission gates, custom editors, compaction strategies, and UI components remain core features that must be built into WinHarness itself. Feature requests in this space become core work, not plugin work.
- MCP configuration and diagnostics become first-class: because MCP is the extension story, failures in server startup, discovery, and tool calls must produce clear, actionable errors.
- Skills and prompt templates carry the "soft" customization load, so their discovery paths remain compatible with the cross-tool `.agents/` convention.
- Project-local resources (skills, `SYSTEM.md`, future prompts, MCP server configs) require the project trust model (roadmap PR-C6) before non-interactive modes execute them, since they are now the primary injection surface.
- If concrete demand for lifecycle hooks emerges (e.g. pre/post tool-call gating by external policy), the candidate escape hatch is hook processes over stdio — git-hook style, spawn-per-event — evaluated in a future ADR. In-process plugin loading stays rejected while the AOT gate stands.
