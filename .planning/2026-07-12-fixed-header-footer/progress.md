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

Phase 2 complete (2026-07-13). `ScreenHeaderFormatter` +
`ScreenFooterFormatter` added (`WinHarness.Cli.Chat`); `ScreenRegionLayout`
gained `Width`/`MinimumWidth`, header/footer truncate to width, and `WriteFixed`
saves/restores the cursor (`ESC 7`/`ESC 8`) so fixed-row paints don't move the
conversation cursor. Wired into `RunReplAsync`/`ReadIdlePrompt`: header +
footer refresh each idle prompt, in-region status line skipped when active.
Default path still unchanged (opt-in + no-op when redirected). Streaming
routing is Phase 3; prompt-into-footer + resize is Phase 4.

## Validation

- Phase 0 spike `--verify` run: `ok=true`, exit 0. Result captured in
  `spike-result.json` and summarized in `findings.md`.
- Phase 1: layout/lifecycle tests; suite 310/0/0.
- Phase 2: 5 new formatter tests + width/narrow-layout test (11 controller tests
  total). `dotnet build` clean (0 warnings). Full suite 315 passed, 0 failed,
  0 skipped. `ESC 7`/`ESC 8` (DECSC/DECRC) is a basic VT100 primitive supported
  by conhost/Windows Terminal; live eyeball test pending Phase 6 manual matrix.

## Errors

None.
