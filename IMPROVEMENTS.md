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

### 1.2 No retry or backoff for provider calls

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

The diagnostic records track `retry.count` (always `"0"`), suggesting retry
instrumentation is planned but not implemented. Transient failures (rate
limits, 5xx, connection resets) from OpenAI-compatible endpoints bubble up as
turn failures with no automatic recovery.

**Recommendation:** Add a configurable retry policy (exponential backoff with
jitter, max 3 attempts) in the provider layer or as `IChatClient` middleware.
Surface retry count in diagnostics.

### 1.3 No mid-turn cancellation from the line-based REPL

**File:** `src/WinHarness.Cli/Program.cs` (`ChatRepl`)

The TUI has a 10-minute `TurnTimeout`, but the line-based REPL offers no way
to interrupt a running turn short of Ctrl+C (which kills the process).

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

### 2.2 Grep binary detection is shallow

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`GrepTool`)

Binary detection only checks the first 1024 bytes for null bytes. UTF-16
files without BOM and some compressed formats will pass through and produce
garbage matches.

**Recommendation:** Increase the sample to 8 KB and also check for a high
ratio of non-printable/control characters (e.g. >30% in the first 8 KB).
Alternatively, use a known list of text file extensions as a fast pre-filter.

### 2.3 Session files grow indefinitely

**File:** `src/WinHarness.Infrastructure/Sessions/JsonlSessionStore.cs`

Append-only JSONL means files grow without bound. Compaction helps context
windows but doesn't reduce on-disk size.

**Recommendation:** Add a `/prune` slash command that rewrites a session file
by dropping entries before the latest compaction boundary. Consider automatic
pruning when a session exceeds a configurable size threshold (e.g. 10 MB).

---

## 3. Feature completeness

### 3.1 Version string is stale

**File:** `src/WinHarness.Cli/Program.cs`

`const string Version = "0.1.0"` but the README describes v0.2 features
(sessions, compaction, TUI, skills, context files).

**Recommendation:** Bump to `"0.2.0"` and keep it in sync with the README
scope table. Consider reading the version from `AssemblyInfo.cs` or a shared
`version.json` to avoid drift.

### 3.2 Prompt caching is declared but unused

**Files:** `src/WinHarness.Abstractions/Providers/ProviderCapabilities.cs`,
`src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

`ProviderCapabilities.PromptCaching` exists but the runtime doesn't use it.
There's no logic to place static content (system prompts, context files) at
positions that benefit from Anthropic-style cache breakpoints or OpenAI's
automatic caching.

**Recommendation:** Either remove the flag until caching is implemented, or
add a lightweight implementation: when `PromptCaching` is true, annotate the
system prompt and context-file messages with cache markers (for Anthropic) or
rely on OpenAI's automatic caching (which requires no client changes).

### 3.3 No multi-model orchestration

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

The runtime uses one provider/model per turn. Multi-model orchestration,
fallback chains, or model routing aren't supported.

**Recommendation:** This is fine for v0.2. For a future release, consider a
`ModelSelector` abstraction that can route based on task type (e.g. cheap
model for summarization, capable model for code generation) or implement a
fallback chain when the primary model fails.

### 3.4 No tool output caching / deduplication

**File:** `src/WinHarness.Core/Runtime/SingleAgentRuntime.cs`

If the model requests the same tool call twice (e.g. `read_file` on the same
path with the same arguments), the tool executes again. This wastes time and
tokens.

**Recommendation:** Add an optional, per-turn tool result cache keyed by
`(toolName, serialized arguments)`. Return the cached result for duplicate
calls within the same turn.

---

## 4. Code quality & maintainability

### 4.1 `Program.cs` is large and mixes concerns

**File:** `src/WinHarness.Cli/Program.cs` (~500 lines)

The CLI entry point contains command handlers, helper methods
(`ParseStringArray`, `ParseStringDictionary`, `MergeToolMetadata`),
`ConfigFileUpdater`, `StarterConfiguration`, `CliValidation`, and the entire
`ChatRepl` class including the `TransientStatusLine` helper.

**Recommendation:** Extract command handlers into separate files per command
group (e.g. `Commands/ConfigCommands.cs`, `Commands/ProviderCommands.cs`,
`Commands/ChatCommand.cs`). Move `ChatRepl` and `TransientStatusLine` to
`Chat/ChatRepl.cs`. Keep `Program.cs` to wiring only.

### 4.2 `BuiltinToolProvider.cs` is large and mixes concerns

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (~400 lines)

Contains the abstract `BuiltinTool` base, all six tool implementations, the
`LocalCapturedCommandExecutor`, and the `PassThroughLongPathService`.

**Recommendation:** Split into separate files per tool under
`src/WinHarness.Tools/Builtin/`. Move `LocalCapturedCommandExecutor` to its
own file. This makes each tool easier to test in isolation and reduces merge
conflicts.

### 4.3 `ChatTuiApp.cs` is very large

**File:** `src/WinHarness.Cli/Tui/ChatTuiApp.cs` (~600 lines)

Handles window construction, transcript management, markdown rendering,
streaming, slash commands, session tree picking, and turn lifecycle.

**Recommendation:** Extract the transcript data source and rendering into a
dedicated `TranscriptController` class. Extract the turn runner into a
`TuiTurnRunner`. Keep `ChatTuiApp` focused on window layout and event wiring.

### 4.4 Missing XML documentation on public APIs

Several public interfaces and classes in `WinHarness.Abstractions` have good
XML docs, but many implementation classes lack them. This is especially
noticeable in `WinHarness.Infrastructure` and `WinHarness.Cli`.

**Recommendation:** Add XML docs to all public and internal types. Enable
`CS1591` as a warning in `Directory.Build.props` (or a scoped
`<GenerateDocumentationFile>true</GenerateDocumentationFile>`) to catch
undocumented public surface over time.

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

### 6.2 No file extension restrictions

**File:** `src/WinHarness.Tools/BuiltinToolProvider.cs` (`WriteFileTool`, `EditFileTool`)

The model can write or edit any file within the workspace. There's no
mechanism to protect `.git`, `.winharness`, or other sensitive paths.

**Recommendation:** Add an optional `protectedPaths` list in
`WinHarnessOptions` (glob patterns). Default could include `.git/**`,
`.winharness/**`. Tools would refuse to operate on matching paths.

---

## Summary

| Area | Items | Risk |
|------|-------|------|
| Operational reliability | 4 | Medium-High |
| Performance | 3 | Medium |
| Feature completeness | 4 | Low-Medium |
| Code quality | 4 | Low |
| Testing | 3 | Medium |
| Security | 2 | Medium |

The recommendations above are ordered for a pragmatic v0.2→v0.3 cycle:
tackle the credential deadlock (1.1) and retry logic (1.2) first, then the
version bump (3.1) and code organization (4.1–4.3), followed by the remaining
items as time allows.
