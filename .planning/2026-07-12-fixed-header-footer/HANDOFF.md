# Handoff — true fixed header/footer in `winharness chat`

_Last updated: 2026-07-13. Branch: `main` (clean, all pushed through `294680b`)._

## What this is

Pin a header row (top) and footer row(s) (bottom) in `winharness chat` so they
stay on screen while conversation output scrolls between them, using a DECSTBM
scroll region and preserving native scrollback (no alt-screen). Full plan +
rationale in `task_plan.md`; investigation notes in `findings.md`; running log
in `progress.md` (this directory: `.planning/2026-07-12-fixed-header-footer/`).

## Status: Phases 0–4 DONE, pushed to `main`. Next = Phase 5

| Phase | State | Commit |
| ------- | ------- | -------- |
| 0 — DECSTBM spike (prove it on real conhost) | ✅ | `ef573d4` |
| 1 — `ScreenRegionController` + lifecycle/teardown | ✅ | `5222853` |
| (lint cleanup: unused using, MD013, spike CS0227→DllImport) | ✅ | `3735e3f` |
| 2 — header/footer content (formatters, width, cursor save/restore) | ✅ | `bb2edd9` |
| 3 — streaming region-safety audit (removed dead erase path) | ✅ | `294680b` |
| 4 — prompt-into-footer, input-loop repaint, resize | ✅ | _this commit_ |
| 5 — harden teardown + capability probe replaces env opt-in | ⬜ next | — |
| 6 — manual test matrix (WT / conhost / VS Code / redirected) | ⬜ | — |
| 7 — AOT publish + manual verify | ⬜ | — |

## How to try it

`$env:WINHARNESS_FIXED_HEADER=1; winharness chat` (opt-in; unset/`0` = today's
behavior). No-op when output redirected or terminal < 6 rows / < 20 cols.

## Key files

- `src/WinHarness.Cli/Rendering/ScreenRegionController.cs` — `ScreenRegionController`
  (Enter/Exit/SetHeader/SetFooter/BeginPrompt/EndPrompt/Repaint/OnResize/Dispose)
  - pure `ScreenRegionLayout` (opt-in/redirect/size math; unit-tested). `WriteFixed`
  uses `ESC 7`/`ESC 8` (DECSC/DECRC) cursor save/restore. `BeginPrompt`/`EndPrompt`
  save+restore the conversation cursor around the prompt-row input loop. `OnResize()`
  re-resolves the layout from `Console.WindowWidth/Height`, re-establishes the
  DECSTBM region on size change, and deactivates (resets region to full screen)
  when the terminal shrinks below the minimum.
- `src/WinHarness.Cli/Chat/ScreenRegionFormatters.cs` — `ScreenHeaderFormatter`
  (`WinHarness chat · provider · model · effort`) + `ScreenFooterFormatter`
  (`md on/off · context · tools`). Plain text (fixed rows are raw `Console.Write`).
- `src/WinHarness.Cli/Program.cs` — lifecycle wired in `RunAsync` (try/finally +
  `Dispose`); `RunReplAsync`/`ReadIdlePrompt` take `ScreenRegionController screen`
  and refresh header/footer each idle prompt (skip in-region status line when active).
- `tests/WinHarness.IntegrationTests/ScreenRegionControllerTests.cs` — 15 tests
  (layout math + formatters + resize/opt-out guards).
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
- `dotnet test WinHarness.sln -c Release` → **319 passed, 0 failed, 0 skipped**
  (Windows; 5 Windows-only tests skip on Linux). +4 tests vs Phase 3 (315).
- Phase 0 spike re-verified `ok=true` after Phase 4 (header/footer intact,
  region confined to rows 3..22 of the 80×24 conhost buffer).

## KNOWN pi-lens noise (NOT real — do not "fix" by changing code)

- `ScreenRegionControllerTests.cs`: `lens_diagnostics mode=all/full` shows ~15
  stale CS0246/CS0103 "type not found" for `ScreenRegion*`/`Screen*Formatter`
  (and after Phase 4, stale CS1061 "no definition for FooterStatusRow/
  FooterPromptRow"). **Proven stale**: dedicated `lsp_diagnostics` on the
  controller file = 0 errors; build + tests green. It's pi-lens's session runner
  cache not invalidating after the internal types/new members were finalized.
  Clears on a fresh pi-lens session.
- `MarkdownConsoleRenderer.cs:431` CS9335 "pattern is redundant": pre-existing
  (commit `3102807`), untouched by this effort, LSP-only (build clean — newer
  Roslyn analyzer in the LSP than SDK 10.0.301). User chose to **leave it**.

## Phase 5 — concrete next steps

1. **Capability probe replaces env opt-in.** `WINHARNESS_FIXED_HEADER` becomes
   a force-ON/force-OFF override; by default probe the terminal for DECSTBM
   support rather than requiring the env var. Candidates: emit a private-mode
   query (`ESC [ ?6n` DA1) / `DECSTBM` round-trip, or detect via the existing
   `Console` APIs / `kernel32` terminal mode. Decide threshold and provide a
   force-off escape hatch for terminals known-bad (logged in `findings.md`).
2. **Harden teardown.** Verify region reset + cursor restore on every exit path:
   normal exit, `/exit`, Ctrl+C, unhandled exception, `AppDomain.ProcessExit`.
   `Exit()`/`Dispose()` already reset; Phase 5 adds the capability-probe-driven
   enter gate and an exception-guarded repaint so a transient console error
   can't kill the REPL.
3. Update `progress.md`/`task_plan.md`, commit, push. Then proceed to Phase 6
   (manual test matrix on WT / conhost / VS Code / redirected).

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
  `Console.OutputEncoding = UTF8` is set in `Enter()` for `·`/`…`.
