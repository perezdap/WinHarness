# Design: Optional `input` (stdin) for `run_command`

**Status:** Proposed
**Target:** WinHarness (post-`2369c5f`)
**Goal:** Let `run_command` feed a fixed string to a captured process's stdin, restoring support
for the legitimate "pipe input in" use case that the stdin-hang fix removed — without
reintroducing the hang.

This builds directly on commit `2369c5f` (`fix(tools): avoid captured command hangs`), which made
captured mode strictly non-interactive by closing stdin so prompts receive EOF instead of blocking.

---

## 1. Problem Statement

`CapturedCommandExecutor` now closes stdin immediately after starting a process. That is correct for
the common agent failure mode (a command prompts for input nobody can supply, then times out), but it
permanently removes the ability to run commands that legitimately consume stdin, e.g.:

- `git apply` / `git am` reading a patch from stdin
- `jq` / `sort` / formatters reading piped text
- a CLI that takes a password or payload via stdin instead of an argument (keeps secrets off the
  process command line)

Today the only workaround is to write a temp file and redirect via a shell, which is clumsy and
Windows-shell-specific.

---

## 2. Goals and Non-Goals

### Goals

1. Add an optional `input` string field to the `run_command` tool schema.
2. When present, write `input` to the child's stdin, then close stdin (EOF) so the process still
   terminates instead of waiting for more.
3. Keep the no-`input` default behavior **exactly as today**: stdin closed immediately, no hang.
4. Stay AOT-safe and pass the existing analyzer/style gate.
5. Cover the new path with regression tests.

### Non-Goals (deferred)

- Streaming / multi-write interactive stdin (that is what `mode: "interactive"` / ConPTY is for).
- Binary stdin payloads (string-only for now; encoding is UTF-8 no BOM).
- Changing interactive (`ConPtyCommandExecutor`) behavior.
- Reading `input` from a file path (callers can inline the content; revisit if needed).

---

## 3. Design

### 3.1 Contract changes

`CommandRequest` (in `WinHarness.Abstractions/Platform/IPlatformServices.cs`) gains an optional
standard-input payload:

```csharp
public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    CommandExecutionMode Mode,
    TimeSpan Timeout,
    string? StandardInput = null);
```

- `null` (default) → preserve current behavior: close stdin immediately, no write.
- non-`null` (including empty string) → write the value to stdin, then close.

Making it an optional positional/`null`-default parameter keeps every existing `new CommandRequest(...)`
call site compiling unchanged.

### 3.2 Executor behavior (`CapturedCommandExecutor`)

Replace the immediate `CloseStandardInput(process)` with a write-then-close helper:

```csharp
await WriteStandardInputAsync(process, request.StandardInput).ConfigureAwait(false);
```

where the helper:

1. If `StandardInput` is non-null, `await process.StandardInput.WriteAsync(value)` then flush, using
   UTF-8 no BOM (match `Utf8NoBom` used elsewhere).
2. Always `process.StandardInput.Close()` afterwards (EOF).
3. Swallow `InvalidOperationException` / `IOException` for the "process already exited" race, mirroring
   the current `CloseStandardInput` guard.

Interactive mode (ConPTY) is untouched and ignores `StandardInput` for this iteration.

### 3.3 Tool schema + parsing (`RunCommandTool` in `BuiltinToolProvider.cs`)

- Add to the JSON schema:
  `"input":{"type":"string","description":"Optional text written to the process's standard input, then EOF. Use for commands that read from stdin (e.g. patches, piped text). Omit for normal commands."}`
- In `ExecuteAsync`, read it with the existing optional-string pattern (a small `OptionalString`
  helper, or inline `TryGetProperty` + `ValueKind == JsonValueKind.String`), defaulting to `null`.
- Pass it through as the new `StandardInput` argument when building `CommandRequest` (inside the
  existing try/catch that already maps validation errors to `invalid_arguments`).

### 3.4 Description tweak

Update the `run_command` tool description to mention that captured mode is non-interactive and that
`input` is the supported way to supply stdin, so the model stops trying interactive prompts.

---

## 4. Affected Files

| File | Change |
|------|--------|
| `src/WinHarness.Abstractions/Platform/IPlatformServices.cs` | Add `string? StandardInput = null` to `CommandRequest`. |
| `src/WinHarness.Platform.Windows/CapturedCommandExecutor.cs` | Write `input` to stdin then close; keep EOF default. |
| `src/WinHarness.Tools/BuiltinToolProvider.cs` | Add `input` to schema, parse it, pass through; tweak description. |
| `tests/WinHarness.IntegrationTests/CommandExecutorTests.cs` | New test: stdin payload is delivered. |
| `tests/WinHarness.IntegrationTests/BuiltinToolProviderTests.cs` | New test: `input` flows to `CommandRequest.StandardInput`. |
| `README.md` | Document the `input` field (tool reference section). |

The `FakeCommandExecutor` already records `LastRequest`, so asserting `StandardInput` flows through
the tool needs no new test scaffolding.

---

## 5. Implementation Phases

### Phase 1 — Contract
- Add `StandardInput` to `CommandRequest`.
- Build the solution to confirm all call sites still compile (optional param = no breakage).

### Phase 2 — Executor
- Implement `WriteStandardInputAsync` and replace `CloseStandardInput` call.
- Keep the exited-process exception guard.

### Phase 3 — Tool surface
- Add `input` to the schema, parse it, pass it through.
- Update the tool description.

### Phase 4 — Tests
- **Executor delivers stdin** (round-trip):
  - Windows: `cmd.exe /c "set /p v=& echo got:%v%"` with `input = "hello\n"` → stdout contains `got:hello`.
  - Linux: `/bin/sh -c "read v; echo got:$v"` with `input = "hello\n"` → stdout contains `got:hello`.
- **Default unchanged**: no `input` → stdin-reading command still completes (EOF), does not time out
  (this is the existing `CapturedExecutorDoesNotHangOnCommandsThatReadStdin` test; keep it green).
- **Tool wiring**: `run_command` with `"input":"hello"` sets `FakeCommandExecutor.LastRequest.StandardInput == "hello"`.

### Phase 5 — Docs + closeout
- Document `input` in `README.md`.
- `dotnet build -c Release` (lint gate) + full `dotnet test`.
- `dotnet publish -r win-x64 -p:PublishAot=true` to confirm AOT.
- Run autoreview closeout.

---

## 6. Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Writing to stdin after the process already exited throws | Guard `WriteAsync`/`Close` with the same exception handling as `CloseStandardInput`. |
| Large `input` could deadlock if the child fills stdout while we're still writing stdin | stdout/stderr are already drained via `ReadToEndAsync` started before the wait; for very large payloads, write stdin concurrently (kick off the write task before awaiting exit) rather than fully sequentially. |
| Model passes `input` in interactive mode expecting it to work | Document that `input` applies to captured mode only; interactive ignores it this iteration. |
| Encoding surprises | Use UTF-8 no BOM consistently; document it. |

---

## 7. Acceptance Criteria

- `run_command` with `input` delivers the text to the process's stdin and the process exits normally.
- `run_command` without `input` behaves exactly as it does after `2369c5f` (no hang).
- New tests pass on Windows; Linux variants pass or skip consistently with existing suite conventions.
- Clean Release build, full test suite green, AOT publish succeeds, autoreview clean.
