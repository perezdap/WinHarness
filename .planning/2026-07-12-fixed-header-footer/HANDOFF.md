# Handoff — true fixed header/footer in `winharness chat`

_Last updated: 2026-07-13. Branch: `main` (clean, all pushed through `294680b`)._

## What this is

Pin a header row (top) and footer row(s) (bottom) in `winharness chat` so they
stay on screen while conversation output scrolls between them, using a DECSTBM
scroll region and preserving native scrollback (no alt-screen). Full plan +
rationale in `task_plan.md`; investigation notes in `findings.md`; running log
in `progress.md` (this directory: `.planning/2026-07-12-fixed-header-footer/`).

## Status: Phases 0–3 DONE, pushed to `main`. Next = Phase 4

| Phase | State | Commit |
| ------- | ------- | -------- |
| 0 — DECSTBM spike (prove it on real conhost) | ✅ | `ef573d4` |
| 1 — `ScreenRegionController` + lifecycle/teardown | ✅ | `5222853` |
| (lint cleanup: unused using, MD013, spike CS0227→DllImport) | ✅ | `3735e3f` |
| 2 — header/footer content (formatters, width, cursor save/restore) | ✅ | `bb2edd9` |
| 3 — streaming region-safety audit (removed dead erase path) | ✅ | `294680b` |
| 4 — prompt-into-footer, input-loop repaint, **resize** | ⬜ next | — |
| 5 — harden teardown + capability probe replaces env opt-in | ⬜ | — |
| 6 — manual test matrix (WT / conhost / VS Code / redirected) | ⬜ | — |
| 7 — AOT publish + manual verify | ⬜ | — |

## How to try it

`$env:WINHARNESS_FIXED_HEADER=1; winharness chat` (opt-in; unset/`0` = today's
behavior). No-op when output redirected or terminal < 6 rows / < 20 cols.

## Key files

- `src/WinHarness.Cli/Rendering/ScreenRegionController.cs` — `ScreenRegionController`
  (Enter/Exit/SetHeader/SetFooter/Repaint/OnResize/Dispose) + pure
  `ScreenRegionLayout` (opt-in/redirect/size math; unit-tested). `WriteFixed`
  uses `ESC 7`/`ESC 8` (DECSC/DECRC) cursor save/restore. `OnResize()` is still
  a **stub** (Phase 4).
- `src/WinHarness.Cli/Chat/ScreenRegionFormatters.cs` — `ScreenHeaderFormatter`
  (`WinHarness chat · provider · model · effort`) + `ScreenFooterFormatter`
  (`md on/off · context · tools`). Plain text (fixed rows are raw `Console.Write`).
- `src/WinHarness.Cli/Program.cs` — lifecycle wired in `RunAsync` (try/finally +
  `Dispose`); `RunReplAsync`/`ReadIdlePrompt` take `ScreenRegionController screen`
  and refresh header/footer each idle prompt (skip in-region status line when active).
- `tests/WinHarness.IntegrationTests/ScreenRegionControllerTests.cs` — 11 tests
  (layout math + formatters).
- `spikes/WinHarness.TerminalRegionSpike/` — DECSTBM proof; `--verify <path>`
  mode forces 80×24 conhost, scrolls through the region, reads the real screen
  buffer back with `ReadConsoleOutputW`, writes JSON. Run:
  `powershell.exe Start-Process <exe> -ArgumentList '"<out.json>"' -Wait` then
  read the json (`ok=true` expected). Uses `[DllImport]` (not LibraryImport) so
  no `AllowUnsafeBlocks`.

## Design decisions locked in (user-approved)

- Header = one row: `WinHarness chat · provider · model · effort`.
- Footer = two rows: **status row** (`md on/off · context · tools`) + **prompt row**.
  Status row is done (Phase 2). Prompt (`›`) still renders in the region — moving
  it into the footer's 2nd row is **Phase 4**.
- Per-turn `UsageFooter` stays in the scroll stream (not pinned).
- Graceful fallback on unsupported terminals (Phase 5 capability probe).
- Layout: header row 1; region rows 2..(H-2); footer = last 2 rows.

## Verification state

- `dotnet build WinHarness.sln -c Release` → **0 errors, 0 warnings** (the lint gate).
- `dotnet test WinHarness.sln -c Release` → **315 passed, 0 failed, 0 skipped**
  (Windows; 5 Windows-only tests skip on Linux).
- Phase 0 spike re-verified `ok=true` after Phase 3.

## KNOWN pi-lens noise (NOT real — do not "fix" by changing code)

- `ScreenRegionControllerTests.cs`: `lens_diagnostics mode=all/full` shows ~15
  stale CS0246/CS0103 "type not found" for `ScreenRegion*`/`Screen*Formatter`.
  **Proven stale**: dedicated `lsp_diagnostics` on that file = 0 diagnostics;
  build + tests green. It's pi-lens's session runner cache not invalidating after
  the internal types were finalized. Clears on a fresh pi-lens session.
- `MarkdownConsoleRenderer.cs:431` CS9335 "pattern is redundant": pre-existing
  (commit `3102807`), untouched by this effort, LSP-only (build clean — newer
  Roslyn analyzer in the LSP than SDK 10.0.301). User chose to **leave it**.

## Phase 4 — concrete next steps

1. **Prompt into the footer.** Footer is 2 rows (region ends at H-2; footer = H-1,
   H). Currently `ReadIdlePrompt` writes `›` at the cursor in the region. Move the
   `›` prompt + typed input onto row H (or H-1), keeping the status line on the
   other footer row. Input editing (`ReadKeyLine`, backspace `\b \b`, etc. in
   `Program.cs` ~L1961/2086/2117) must operate on the fixed prompt row without
   disturbing the region — likely position the cursor to the footer prompt row
   before the key loop and keep it there.
2. **Input-loop footer repaint.** `ReadKeyLine` (idle) and the steering poll in
   `RunTurnWithSteeringAsync` (`TryReadSteeringInput`, ~L1806+) should repaint the
   footer as needed and keep the cursor in-region during a turn.
3. **Resize handling (the real work).** No `SIGWINCH` on Windows — poll
   `Console.WindowWidth/Height`. Implement `ScreenRegionController.OnResize()`:
   recompute layout, reset DECSTBM region, repaint header/footer. Call it from the
   idle key loop and the steering poll (before rendering the prompt each tick).
   Decide behavior when the terminal shrinks below `MinimumHeight`/`MinimumWidth`
   mid-session (fall back to inactive + restore).
4. Add tests for any new pure logic (e.g. resize → new layout) headlessly; keep
   the console I/O thin. Update `progress.md`/`task_plan.md`, commit, push.

## Gotchas learned

- pi-lens LSP does NOT reload csproj/cross-project changes mid-session; trust
  `dotnet build` + `dotnet test` as the source of truth (per AGENTS.md).
- Spike `--verify` window flashes ~1s; that's expected. `cmd /c start` fails
  ("Access is denied") from git-bash — use `powershell.exe Start-Process -Wait`.
- Markdown auto-fix (markdownlint) runs on `.md` edits and can reflow/shift lines;
  re-read before further edits. MD013 disabled repo-wide in `.editorconfig`.
- Header/footer are plain text (raw `Console.Write`), so no Spectre markup there;
  `Console.OutputEncoding = UTF8` is set in `Enter()` for `·`/`…`.
