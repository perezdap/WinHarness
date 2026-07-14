# Handoff — true fixed header/footer in `winharness chat`

_Last updated: 2026-07-13. Branch: `main` (clean, all pushed through `a1547c9`)._

## What this is

Pin a header row (top) and footer row(s) (bottom) in `winharness chat` so they
stay on screen while conversation output scrolls between them, using a DECSTBM
scroll region and preserving native scrollback (no alt-screen). Full plan +
rationale in `task_plan.md`; investigation notes in `findings.md`; running log
in `progress.md` (this directory: `.planning/2026-07-12-fixed-header-footer/`).

## Status: Phases 0–5 DONE, pushed to `main`. Next = Phase 6

| Phase | State | Commit |
| ------- | ------- | -------- |
| 0 — DECSTBM spike (prove it on real conhost) | ✅ | `ef573d4` |
| 1 — `ScreenRegionController` + lifecycle/teardown | ✅ | `5222853` |
| (lint cleanup: unused using, MD013, spike CS0227→DllImport) | ✅ | `3735e3f` |
| 2 — header/footer content (formatters, width, cursor save/restore) | ✅ | `bb2edd9` |
| 3 — streaming region-safety audit (removed dead erase path) | ✅ | `294680b` |
| 4 — prompt-into-footer, input-loop repaint, resize | ✅ | `a1547c9` |
| 5 — harden teardown + capability probe replaces env opt-in | ✅ | _this commit_ |
| 6 — manual test matrix (WT / conhost / VS Code / redirected) | ⬜ next | — |
| 7 — AOT publish + manual verify | ⬜ | — |

## How to try it

`winharness chat` now **auto-enables** the fixed header/footer on terminals
that honor virtual-terminal processing (probed via the console configurator —
`ENABLE_VIRTUAL_TERMINAL_PROCESSING` on Windows). Override with
`WINHARNESS_FIXED_HEADER`: `1` forces on (skip the probe), `0` forces off
(the escape hatch for terminals that mis-detect or where the feature misbehaves).
No-op when output redirected or terminal < 6 rows / < 20 cols.

## Key files

- `src/WinHarness.Cli/Rendering/ScreenRegionController.cs` — `ScreenRegionController`
  (Enter/Exit/SetHeader/SetFooter/BeginPrompt/EndPrompt/Repaint/OnResize/Dispose)
  - pure `ScreenRegionLayout` (opt-in/redirect/size math; unit-tested) + pure
  `ResolveOptIn` (env-override → capability-probe decision; unit-tested). `WriteFixed`
  uses `ESC 7`/`ESC 8` (DECSC/DECRC) cursor save/restore. `BeginPrompt`/`EndPrompt`
  save+restore the conversation cursor around the prompt-row input loop. `OnResize()`
  re-resolves the layout from `Console.WindowWidth/Height`, re-establishes the
  DECSTBM region on size change, and deactivates (resets region to full screen)
  when the terminal shrinks below the minimum. `Enter`/`Exit` are idempotent
  (guarded by an `_entered` flag, not `IsActive`), restore the prior output
  encoding, and `Dispose` is exception-safe so a teardown failure on an exit /
  Ctrl+C / exception path can't leave the terminal worse off.
- `src/WinHarness.Abstractions/Platform/IPlatformServices.cs` — added
  `IAnsiConsoleConfigurator.IsVirtualTerminalEnabled` (probe).
- `src/WinHarness.Platform.Windows/WindowsAnsiConsoleConfigurator.cs` — implements
  the probe: on Windows checks `ENABLE_VIRTUAL_TERMINAL_PROCESSING` is set on the
  output handle via the existing `GetConsoleMode` P/Invoke; on non-Windows
  returns `true` (xterm-compatible terminals honor DECSTBM).
- `src/WinHarness.Cli/Chat/ScreenRegionFormatters.cs` — `ScreenHeaderFormatter`
  (`WinHarness chat · provider · model · effort`) + `ScreenFooterFormatter`
  (`md on/off · context · tools`). Plain text (fixed rows are raw `Console.Write`).
- `src/WinHarness.Cli/Program.cs` — lifecycle wired in `RunAsync` (try/finally +
  `Dispose`); `ScreenRegionController.Create(services.GetRequiredService<IAnsiConsoleConfigurator>())`
  resolves the capability probe via DI; `RunReplAsync`/`ReadIdlePrompt` take
  `ScreenRegionController screen` and refresh header/footer each idle prompt
  (skip in-region status line when active).
- `tests/WinHarness.IntegrationTests/ScreenRegionControllerTests.cs` — 19 tests
  (layout math + formatters + resize/opt-out guards + probe/override resolution +
  idempotent teardown).
- `spikes/WinHarness.TerminalRegionSpike/` — DECSTBM proof; `--verify <path>`
  mode forces 80×24 conhost, scrolls through the region, reads the real screen
  buffer back with `ReadConsoleOutputW`, writes JSON. Run:
  `powershell.exe Start-Process <exe> -ArgumentList '"<out.json>"' -Wait` then
  read the json (`ok=true` expected). Uses `[DllImport]` (not LibraryImport) so
  no `AllowUnsafeBlocks`.

## Design decisions locked in (user-approved)

- Header = one row: `WinHarness chat · provider · model · effort`.
- Footer = two rows: **status row** (`md on/off · context · tools`) + **prompt row**.
  Status row paints terminal row `H-1`; the `›` prompt + typed input live on
  the last row `H` (outside the DECSTBM region) so conversation output never
  overwrites them and typed input never scrolls the region. Both done in
  Phase 4.
- Per-turn `UsageFooter` stays in the scroll stream (not pinned).
- Graceful fallback on unsupported terminals (Phase 5 capability probe);
  Phase 4 also falls back to the scrolling status-line path if the terminal
  shrinks below `MinimumHeight`/`MinimumWidth` mid-session.
- Layout: header row 1; region rows 2..(H-2); footer = last 2 rows.

## Verification state

- `dotnet build WinHarness.sln -c Release` → **0 errors, 0 warnings** (the lint gate).
- `dotnet test WinHarness.sln -c Release` → **323 passed, 0 failed, 0 skipped**
  (Windows; 5 Windows-only tests skip on Linux). +4 tests vs Phase 4 (319).
- Phase 0 spike re-verified `ok=true` after Phase 5 (header/footer intact,
  region confined to rows 3..22 of the 80×24 conhost buffer).

## KNOWN pi-lens noise (NOT real — do not "fix" by changing code)

- `ScreenRegionControllerTests.cs`: `lens_diagnostics mode=all/full` shows ~15
  stale CS0246/CS0103 "type not found" for `ScreenRegion*`/`Screen*Formatter`,
  plus stale CS1061 "no definition for FooterStatusRow/FooterPromptRow"
  (Phase 4) and "no definition for ResolveOptIn" / (on the controller)
  `IsVirtualTerminalEnabled` (Phase 5). **Proven stale**: dedicated
  `lsp_diagnostics` on the controller file = 0 errors for `ResolveOptIn`; the
  `IsVirtualTerminalEnabled` CS1061 is the same root cause — pi-lens's LSP
  server does not reload cross-project interface changes mid-session
  (`WinHarness.Abstractions` gained the member; the CLI build sees it). Build
  - tests green. Clears on a fresh pi-lens session. Trust `dotnet build` +
  `dotnet test` as the source of truth (per AGENTS.md).
- `MarkdownConsoleRenderer.cs:431` CS9335 "pattern is redundant": pre-existing
  (commit `3102807`), untouched by this effort, LSP-only (build clean — newer
  Roslyn analyzer in the LSP than SDK 10.0.301). User chose to **leave it**.

## Phase 5 (done) — what landed

1. **Capability probe replaces the env opt-in.** `WINHARNESS_FIXED_HEADER` is
  now an override, not a hard gate: **unset → trust the probe** (auto-enable on
  terminals that honor VT sequences), `1`/`true` → force on, `0`/`false` → force
  off (escape hatch). The probe reuses the existing console-configurator path:
  added `IAnsiConsoleConfigurator.IsVirtualTerminalEnabled`, implemented in
  `WindowsAnsiConsoleConfigurator` as a `GetConsoleMode` check that
  `ENABLE_VIRTUAL_TERMINAL_PROCESSING` (0x0004) is set on the output handle (on
  Windows — the startup `EnableAnsi()` tried to set it; if the terminal
  accepted it, DECSTBM is honored); non-Windows returns `true` (xterm-compatible).
  `ScreenRegionController.Create(IAnsiConsoleConfigurator)` resolves it via DI;
  the pure three-state decision lives in `ScreenRegionController.ResolveOptIn`
  (unit-tested). This means the feature is on by default in modern Windows
  Terminal / conhost and off the moment output is redirected or the terminal
  is pre-Win10-Threshold.
2. **Hardened teardown.** `Enter` now captures the prior `Console.OutputEncoding`
  (restored on `Exit`) and sets an `_entered` flag; `Exit` keys off `_entered`
  (not `IsActive`) so it still restores after `OnResize` deactivated the layout
  mid-session, resets the scroll region via the parameter-less `ESC [ r`
  (robust when `Layout.Height` is 0 after a shrink-below-minimum, where the
  old `SetRegion(1, Layout.Height)` would emit an invalid `ESC [ 1;0 r`),
  restores the cursor visibility and the encoding. `Dispose` wraps `Exit` in
  try/catch (`IOException` / `PlatformNotSupportedException`) so a teardown
  failure on an exit / Ctrl+C / exception / `AppDomain.ProcessExit` path can't
  leave the terminal in a worse state. Idempotent: double-`Dispose` /
  `Exit`-without-`Enter` are safe no-ops.
3. Tests: +4 headless tests (probe-defaults-to-capability, force-off wins,
  force-on wins, idempotent teardown). Suite 319 → 323, 0 failed. Phase 0 spike
  re-run `ok=true`. Build clean (0 warnings).

### Phase 5 — what is NOT done (deferred)

- **No `AppDomain.ProcessExit` / `ProcessCancellation` hook.** The REPL's
  `try/finally` in `RunAsync` covers normal exit, `/exit`, Ctrl+C (the
  `ConsoleCancelEventHandler` keeps the process alive so the `finally` runs),
  and unhandled exceptions inside the REPL. A hard kill (`Environment.FailFast`,
  `SIGKILL`, OOM) cannot restore the terminal regardless — those paths leave
  the scroll region set. Acceptable: the terminal resets on next prompt render
  or process restart; revisit only if it bites in the manual matrix (Phase 6).
- **No `WINDOW_BUFFER_SIZE_EVENT` hook.** Resize while blocked in the idle
  `ReadKey` loop is still detected at the next keypress / prompt / turn tick
  (carried over from Phase 4).

## Phase 6 — concrete next steps

1. **Manual test matrix.** Run `winharness chat` (no env var now — auto-enables)
   and exercise a turn on each target, then `winharness chat > out.txt` for the
   redirected case:
   - Windows Terminal (PowerShell) — primary target; expect VT probe on,
      header/footer pinned, prompt on the last row.
   - conhost.exe (legacy) — VT on for Win10+ (probe on), off pre-Threshold
      (probe off → falls back to scrolling status line).
   - VS Code integrated terminal — confirm the probe result and that resize
      during a turn re-establishes the region.
   - Git Bash / MSYS pty if used.
   - redirected (`winharness chat > out.txt`) — must be unchanged (no region,
      no fixed rows, plain scrolling output).
   For each: type a prompt, let a turn stream, resize mid-turn, submit steering,
   `/model` + `/md` (header/footer should refresh), `/exit`. Verify the terminal
   is restored on exit (no leftover scroll region, cursor visible).
2. **Force-off sanity.** `WINHARNESS_FIXED_HEADER=0 winharness chat` must behave
   exactly like the shipped scrolling-status-line experience on a VT-capable
   terminal (the escape hatch).
3. Record results in `findings.md`; file follow-ups for any terminal where the
   probe is wrong or the region misbehaves. Then Phase 7: AOT publish + manual
   verify of the published binary.

## Phase 4 (done) — what landed

1. **Prompt into the footer.** Footer is now genuinely two rows: status on row
   `H-1`, prompt on the last row `H`. `ReadIdlePrompt` (active path) calls
   `screen.BeginPrompt()` (DECSC save + position on row `H` + clear + write
   `›`), runs `ReadKeyLine(controlCancels:false, submitNewline:false)` so the
   key loop echoes typed input on the prompt row with no newline on submit (a
   newline on the terminal's last row is the one cursor move DECSTBM does not
   cleanly handle), then `screen.EndPrompt()` clears the row and DECRC-restores
   the conversation cursor so the next turn streams into the region.
2. **Input-loop repaint.** `ReadIdlePrompt` calls `screen.OnResize()` before
   repainting (so a resize since the last prompt is reflected, or deactivates
   if the terminal shrank below the minimum). `RunTurnWithSteeringAsync` now
   takes `ScreenRegionController screen` and calls `OnResize()` once per
   steering tick — cheap early-return when unchanged, re-establishes the region
   when it has. Steering input (`TryReadSteeringInput`) stays inline in the
   region (deliberately not moved to the footer during a turn).
3. **Resize.** `ScreenRegionController.OnResize()` re-resolves the layout from
   the current `Console.WindowWidth/Height`. Unchanged size → early return.
   Shrink below `MinimumHeight`/`MinimumWidth` (or redirected) → reset the
   scroll region to full screen (`ESC [ r`) + deactivate, so `ReadIdlePrompt`
   falls back to the shipped scrolling status-line path. Resize-while-active →
   re-establish DECSTBM + repaint fixed rows (conversation content not reflowed;
   acceptable for an opt-in feature). The opt-in/redirected flags are retained
   on the controller so `OnResize` can re-resolve (new internal ctor).
4. Tests: +4 headless tests (footer row layout, region-end-before-footer,
   inactive default rows, opted-out `OnResize` no-op). Suite 315 → 319.

### Phase 4 known limitations (deferred to Phase 5+)

- Resize while **blocked** in the idle `ReadKey` loop is not detected until the
  next keypress / next prompt or turn (no `WINDOW_BUFFER_SIZE_EVENT` hook yet).
  `OnResize` is polled from the steering loop (during turns) and before each
  idle prompt.
- Long typed input that wraps past the terminal width on the prompt row is not
  hard-clamped (it can wrap into the status row); acceptable for an opt-in
  feature, revisit if it bites in the manual matrix (Phase 6).

## Gotchas learned

- pi-lens LSP does NOT reload csproj/cross-project changes mid-session; trust
  `dotnet build` + `dotnet test` as the source of truth (per AGENTS.md).
- Spike `--verify` window flashes ~1s; that's expected. `cmd /c start` fails
  ("Access is denied") from git-bash — use `powershell.exe Start-Process -Wait`.
- Markdown auto-fix (markdownlint) runs on `.md` edits and can reflow/shift lines;
  re-read before further edits. MD013 disabled repo-wide in `.editorconfig`.
- Header/footer are plain text (raw `Console.Write`), so no Spectre markup there;
  `Console.OutputEncoding = UTF8` is set in `Enter()` for `·`/`…`; Phase 5
  captures the prior encoding and `Exit` restores it (the startup configurator
  also sets UTF-8, so in practice this is a no-op, but teardown is now symmetric).
- The console-mode P/Invoke (`GetConsoleMode`/`SetConsoleMode`) already lives in
  `WindowsAnsiConsoleConfigurator`; the Phase 5 probe reuses it rather than
  duplicating the DllImport/LibraryImport in the CLI project (the AOT gate stays
  clean via `LibraryImport`).
