# WinHarness — Improvement Recommendations

This document captures actionable recommendations from a codebase review of
WinHarness at the v0.2 feature level. Items are grouped by risk/impact and
ordered roughly by priority within each group.

---

## 1. Operational reliability

### 1.1 Synchronous credential resolution can deadlock

**File:** `src/WinHarness.Providers/OpenAiCompatibleProviderFactory.cs`

`ResolveApiKey()` calls `.GetAwaiter().GetResult()` on the async
`ICredentialStore.GetSecretAsync`. In single-threaded synchronization contexts
this can deadlock. The factory's `Create()` is synchronous but is called from
the runtime's async pipeline.

**Recommendation:** Either make `IProviderFactory.Create` async (preferred) or
resolve and cache credentials eagerly at startup so the hot path is
synchronous and safe.

**Reviewer note:** Prefer the async `Create` over the eager-cache escape
hatch. The eager cache doesn't fix the architectural smell — it just moves
the sync-over-async to startup, where a missing credential still throws. Make
`Create` async and let the runtime `await` it; the call site is already in an
async pipeline.

### 1.2 No retry or backoff for provider calls

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

The diagnostic records track `retry.count` (always `"0"`), suggesting retry
instrumentation is planned but not implemented. Transient failures (rate
limits, 5xx, connection resets) from OpenAI-compatible endpoints bubble up as
turn failures with no automatic recovery.

**Recommendation:** Add a configurable retry policy (exponential backoff with
jitter, max 3 attempts) in the provider layer or as `IChatClient` middleware.
Surface retry count in diagnostics.

**Reviewer note:** Implement as `IChatClient` middleware, not in the provider
layer. The middleware sits inside the runtime's cancellation pipeline, so
retry respects the active `CancellationToken` automatically. Also: make
retry opt-in for non-idempotent streamed turns to avoid double-billing on
partial failures.

### 1.3 No mid-turn cancellation from the line-based REPL

**File:** `src/WinHarness.Cli/Program.cs` (`ChatRepl`)

The line-based REPL offers no way to interrupt a running turn short of
Ctrl+C (which kills the process).

**Recommendation:** Register a `Console.CancelKeyPress` handler that signals a
`CancellationToken` for the active turn without terminating the process.
Display "Interrupted" and save any partial response to the session.

### 1.4 Linux has no credential store implementation

**File:** `src/WinHarness.Platform.Windows/WindowsCredentialStore.cs`

Only `WindowsCredentialStore` exists. On Linux, `ICredentialStore` has no
implementation — API keys must be passed inline or stored in config files
(which the README explicitly warns against).

**Recommendation:** Provide a Linux credential store backed by
`libsecret`/`secret-tool` or, as a fallback, an encrypted file with a
user-visible warning. Register the appropriate implementation via
`RuntimeInformation.IsOSPlatform`.

**Reviewer note:** Design the encrypted-file store first and treat `libsecret`
as the nice-to-have. `libsecret` requires a running secret-service daemon,
which is often absent on headless Linux and CI. For many users the file store
isn't a "fallback" — it's the primary path.

---

## 2. Performance

### 2.1 Glob enumerates all files before filtering

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`GlobTool`)

`GlobTool` calls `Directory.EnumerateFiles(WorkspaceRoot, "*", SearchOption.AllDirectories)`
and then filters in-memory with `FileSystemName.MatchesSimpleExpression`. For
large repositories this walks every file on disk.

**Recommendation:** When the glob pattern has a fixed prefix (e.g.
`src/**/*.cs`), pass the prefix directory to `EnumerateFiles` to reduce the
walk. For simple patterns without `**`, pass the pattern directly to
`EnumerateFiles` and skip the in-memory filter entirely.

**Reviewer note:** The bigger win is a default ignore list. The current walk
descends into `.git`, `bin/`, `obj/`, and `node_modules/` unconditionally.
Respecting `.gitignore` (with a built-in default for non-git workspaces)
would dwarf the prefix-pruning savings on most repos.

### 2.2 Grep binary detection is shallow

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`GrepTool`)

Binary detection only checks the first 1024 bytes for null bytes. UTF-16
files without BOM and some compressed formats will pass through and produce
garbage matches.

**Recommendation:** Increase the sample to 8 KB and also check for a high
ratio of non-printable/control characters (e.g. >30% in the first 8 KB).
Alternatively, use a known list of text file extensions as a fast pre-filter.

**Reviewer note:** Flip the priority: lead with the known-text-extensions
allowlist as the fast path (skip the byte scan entirely for `.cs`, `.md`,
`.json`, etc.) and treat the control-char heuristic as the fallback for
extensionless or unknown files. Heuristics on bytes are unpredictable; the
allowlist is fast and deterministic.

### 2.3 Session files grow indefinitely

**File:** `src/WinHarness.Infrastructure/Sessions/JsonlSessionStore.cs`

Append-only JSONL means files grow without bound. Compaction helps context
windows but doesn't reduce on-disk size.

**Recommendation:** Add a `/prune` slash command that rewrites a session file
by dropping entries before the latest compaction boundary. Consider automatic
pruning when a session exceeds a configurable size threshold (e.g. 10 MB).

**Reviewer note:** Append-only JSONL is a feature, not just a bug — it's what
makes resume and tree-branching safe. Keep pruning manual and explicit; don't
auto-trim. Auto-pruning at 10 MB risks throwing away compaction history that's
still useful for "what did the model think earlier?" debugging.

---

## 3. Feature completeness

### 3.1 Version string is stale

**File:** `src/WinHarness.Cli/Program.cs`

`const string Version = "0.1.0"` but the README describes v0.2 features
(sessions, compaction, skills, context files).

**Recommendation:** Bump to `"0.2.0"` and keep it in sync with the README
scope table. Consider reading the version from `AssemblyInfo.cs` or a shared
`version.json` to avoid drift.

**Reviewer note:** Go further: read from `AssemblyInformationalVersion`
driven by `Directory.Build.props`, so CI/minver can own the version and the
README scope table references a single source of truth. A `version.json`
works too; the point is to never hand-edit the version string in `Program.cs`
again.

### 3.2 Prompt caching is declared but unused

**Files:** `src/WinHarness.Abstractions/Providers/ProviderCapabilities.cs`,
`src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

`ProviderCapabilities.PromptCaching` exists but the runtime doesn't use it.
There's no logic to place static content (system prompts, context files) at
positions that benefit from Anthropic-style cache breakpoints or OpenAI's
automatic caching.

**Recommendation:** Remove the `PromptCaching` flag for v0.2. A capability
flag that the runtime ignores is worse than no flag — it lies to anyone
reading `ProviderCapabilities` to decide what to do. Re-add it when there's a
real implementation behind it (Anthropic cache breakpoints, or rely on
OpenAI's automatic caching which requires no client changes).

### 3.3 No multi-model orchestration

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

The runtime uses one provider/model per turn. Multi-model orchestration,
fallback chains, or model routing aren't supported.

**Recommendation:** This is fine for v0.2. For a future release, consider a
`ModelSelector` abstraction that can route based on task type (e.g. cheap
model for summarization, capable model for code generation) or implement a
fallback chain when the primary model fails.

**Reviewer note:** Keep this out until there's a real user need. Adding a
`ModelSelector` abstraction now would be speculative surface area. Revisit
only when a concrete workflow (e.g. summarize-then-edit) is bottlenecked on
model cost or capability.

### 3.4 No tool output caching / deduplication

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

If the model requests the same tool call twice (e.g. `read_file` on the same
path with the same arguments), the tool executes again. This wastes time and
tokens.

**Recommendation:** Add an optional, per-turn tool result cache keyed by
`(toolName, serialized arguments)`. Return the cached result for duplicate
calls within the same turn.

**Reviewer note:** Be careful here. Caching `read_file` or `grep` results
within a turn is safe; caching `run_command` or `edit_file` is a footgun.
The cache key needs a pure-vs-side-effecting distinction, and most tools
aren't pure. Skip this unless transcript analysis shows real duplicate-call
waste.

---

## 4. Code quality & maintainability

### 4.1 `Program.cs` is large and mixes concerns

**File:** `src/WinHarness.Cli/Program.cs` (~1014 lines; was estimated ~500)

The CLI entry point contains command handlers, helper methods
(`ParseStringArray`, `ParseStringDictionary`, `MergeToolMetadata`),
`ConfigFileUpdater`, `StarterConfiguration`, `CliValidation`, and the entire
`ChatRepl` class including the `TransientStatusLine` helper.

**Recommendation:** Extract command handlers into separate files per command
group (e.g. `Commands/ConfigCommands.cs`, `Commands/ProviderCommands.cs`,
`Commands/ChatCommand.cs`). Move `ChatRepl` and `TransientStatusLine` to
`Chat/ChatRepl.cs`. Keep `Program.cs` to wiring only.

**Reviewer note:** The actual line count is roughly 2× the original estimate,
which strengthens the case. The existing `app.Add("...")` registration style
maps cleanly to a per-command-group file split.

### 4.2 `BuiltinToolProvider.cs` is large and mixes concerns

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (~545 lines; was estimated ~400)

Contains the abstract `BuiltinTool` base, all six tool implementations, the
`LocalCapturedCommandExecutor`, and the `PassThroughLongPathService`.

**Recommendation:** Split into separate files per tool under
`src/WinHarness.Tools/Builtin/`. Move `LocalCapturedCommandExecutor` to its
own file. This makes each tool easier to test in isolation and reduces merge
conflicts.

### 4.3 Missing XML documentation on public APIs

Several public interfaces and classes in `WinHarness.Abstractions` have good
XML docs, but many implementation classes lack them. This is especially
noticeable in `WinHarness.Infrastructure` and `WinHarness.Cli`.

**Recommendation:** Add XML docs to all public and internal types. Enable
`CS1591` as a warning in `Directory.Build.props` (or a scoped
`<GenerateDocumentationFile>true</GenerateDocumentationFile>`) to catch
undocumented public surface over time.

**Reviewer note:** Don't enable `CS1591` solution-wide while
`TreatWarningsAsErrors` is on (per `AGENTS.md`) — that would break the build
on day one. Scope it to `WinHarness.Abstractions` only and ratchet outward as
docs are filled in.

---

## 5. Testing

### 5.1 No unit tests for `SessionContextBuilder`

**File:** `src/WinHarness.Core/Sessions/SessionContextBuilder.cs`

This is pure logic (no I/O) and should be trivially unit-testable. It handles
compaction overlay, skill system prompts, and branch-to-conversation
projection — all of which are correctness-critical.

**Recommendation:** Add unit tests covering: empty branch, single message,
multiple messages, compaction boundary (summary injected, messages before
boundary excluded), and skill system prompt injection.

**Reviewer note:** Highest-leverage of the three testing items. Compaction
overlay correctness is the kind of bug that silently corrupts sessions — do
this one first.

### 5.2 No unit tests for `TurnRecorderChatClient`

**File:** `src/WinHarness.Core/Runtime/TurnRecorderChatClient.cs`

The turn recording logic (new-turn detection, tool result capture, artifact
building) is complex and currently only exercised indirectly through
integration tests.

**Recommendation:** Add focused unit tests with a mock `IChatClient` inner
that returns known responses. Verify that `BuildTurnArtifacts` produces the
correct `ConversationMessage` sequence for: text-only response, single tool
call, multiple tool round-trips, and streaming responses.

### 5.3 No tests for `GlobTool` or `GrepTool` in isolation

**File:** `tests/WinHarness.IntegrationTests/BuiltinToolProviderTests.cs`

The built-in tool tests exist but it's unclear whether glob and grep are
covered with edge cases (nested directories, binary files, large files,
pattern edge cases).

**Recommendation:** Add targeted tests: glob with `**` recursive patterns,
glob with no matches, grep on binary files (should skip), grep with regex
timeout, grep with file size exceeding `maxFileBytes`.

---

## 6. Security

### 6.1 No command allowlist/denylist

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`RunCommandTool`)

Any executable the model requests can be run (subject to the workspace
sandbox). There's no mechanism to restrict dangerous commands.

**Recommendation:** Add an optional `commandAllowlist` and `commandDenylist`
in `WinHarnessOptions`. Default denylist could include `format`, `del /f`,
`rmdir /s`. Allowlist mode would restrict to an explicit set of executables.

**Reviewer note:** A denylist is a false sense of security — the model can
equally do damage with `Remove-Item -Recurse`, `cmd /c`, or by writing a
`.ps1` and running it. Reframe this as "add a confirmation prompt for
`run_command` outside an allowlist" rather than "block known-dangerous
commands." The allowlist mode is the useful half; the denylist is theater.

### 6.2 No file extension restrictions

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`WriteFileTool`, `EditFileTool`)

The model can write or edit any file within the workspace. There's no
mechanism to protect `.git`, `.winharness`, or other sensitive paths.

**Recommendation:** Add an optional `protectedPaths` list in
`WinHarnessOptions` (glob patterns). Default could include `.git/**`,
`.winharness/**`. Tools would refuse to operate on matching paths.

**Reviewer note:** This is the more useful of the two security items — file
path restrictions are predictable and enforceable in a way that command
denylists aren't. Pair with the same confirmation-prompt mechanism as 6.1 so
that `write_file`/`edit_file` on a protected path can be approved at runtime
rather than just refused.

---

## 7. Observability

### 7.1 Diagnostics surface is unaudited

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

The runtime emits a diagnostic record (e.g. `retry.count` at line 420) but
the full set of emitted fields has not been audited. Before adding retry
(1.2), tool caching (3.4), or re-adding prompt caching (3.2), it's worth a
coherent "what should diagnostics expose?" pass so that retry count, cache
hit/miss, and tool-call counts land in one place instead of accruing
piecemeal.

**Recommendation:** Audit the current diagnostics emission points and publish
a documented schema (field names, types, when emitted). Then add retry,
caching, and tool-count fields as a single coordinated change rather than
three separate ad-hoc additions.

---

## Summary

| Area | Items | Risk |
|------|-------|------|
| Operational reliability | 4 | Medium-High |
| Performance | 3 | Medium |
| Feature completeness | 4 | Low-Medium |
| Code quality | 4 | Low-Medium |
| Testing | 3 | Medium |
| Security | 2 | Medium |
| Observability | 1 | Medium |

The recommendations above are ordered for a pragmatic v0.2→v0.3 cycle:
tackle the credential deadlock (1.1) and retry logic (1.2 as `IChatClient`
middleware) first, slip the `PromptCaching` flag removal (3.2) in alongside
the version bump (3.1), then the code organization splits (4.1–4.3, with 4.3
the highest-impact), then the observability audit (7.1) so subsequent retry
and caching work lands coherently, followed by the remaining items as time
allows.
