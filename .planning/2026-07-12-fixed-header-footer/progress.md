# Progress: true fixed header/footer

## 2026-07-12

- Created active planning workspace: `.planning/2026-07-12-fixed-header-footer/`.
- Set `.planning/.active_plan` to `2026-07-12-fixed-header-footer`.
- Reviewed relevant current code paths:
  - `Program.cs` idle prompt and steering input loops.
  - `AssistantStreamWriter` row tracking / erase logic.
  - `ThinkingIndicator` single-line spinner behavior.
- Wrote initial implementation plan around DECSTBM scroll regions and main-buffer preservation.

## Current status

Phase 4 complete (2026-07-13). The footer is now genuinely two rows: status on
row `H-1` and the `›` prompt + typed input on the last row `H` (outside the
DECSTBM region), so typed input never scrolls the conversation. Added
`ScreenRegionController.BeginPrompt`/`EndPrompt` (DECSC save → position on
row `H` → write `›` … clear row → DECRC restore) and a `submitNewline`
parameter on the shared `ReadKeyLine` (the active idle path passes `false` so no
newline is written on the terminal's last row). `ReadIdlePrompt` calls `OnResize`
before repainting; `RunTurnWithSteeringAsync` takes `screen` and calls `OnResize`
per steering tick. Implemented `OnResize`: re-resolve layout, early-return on
unchanged size, re-establish region + repaint on resize-while-active, deactivate
(reset region to full screen) on shrink-below-minimum so callers fall back to
the shipped scrolling status-line path. Steering input stays inline in the
region during a turn (deliberately not moved to the footer). Next: Phase 5
(capability probe replaces env opt-in; harden teardown).

### Earlier: Phase 2 complete (2026-07-13)

`ScreenHeaderFormatter` + `ScreenFooterFormatter` added (`WinHarness.Cli.Chat`);
`ScreenRegionLayout` gained `Width`/`MinimumWidth`, header/footer truncate to
width, and `WriteFixed` saves/restores the cursor (`ESC 7`/`ESC 8`) so fixed-row
paints don't move the conversation cursor. Wired into `RunReplAsync`/
`ReadIdlePrompt`: header + footer refresh each idle prompt, in-region status
line skipped when active. Default path still unchanged (opt-in + no-op when
redirected).

## Validation

- Phase 0 spike `--verify` run: `ok=true`, exit 0. Result captured in
  `spike-result.json` and summarized in `findings.md`.
- Phase 1: layout/lifecycle tests; suite 310/0/0.
- Phase 2: 5 new formatter tests + width/narrow-layout test (11 controller tests
  total). `dotnet build` clean (0 warnings). Full suite 315 passed, 0 failed,
  0 skipped. `ESC 7`/`ESC 8` (DECSC/DECRC) is a basic VT100 primitive supported
  by conhost/Windows Terminal; live eyeball test pending Phase 6 manual matrix.
- Phase 3: removed dead region-unsafe `TryEraseForReRender`; build clean; suite
  315/0/0; Phase 0 spike re-run `ok=true` (header/footer survive cursor-relative
  scroll — the same write pattern the streaming path uses).
- Phase 4: added `BeginPrompt`/`EndPrompt` + `submitNewline` on `ReadKeyLine`;
  prompt moved into footer row `H`; implemented `OnResize` (re-resolve, re-establish
  region, deactivate below minimum); `RunTurnWithSteeringAsync` polls `OnResize`
  per tick. +4 headless tests; build clean (0 warnings); suite 315 → 319 passed,
  0 failed, 0 skipped. Phase 0 spike re-run `ok=true`.

## Errors

None.
