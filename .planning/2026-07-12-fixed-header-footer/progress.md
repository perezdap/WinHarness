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

Phase 1 complete (2026-07-13). `ScreenRegionController` + `ScreenRegionLayout`
introduced under `src/WinHarness.Cli/Rendering/`, lifecycle wired into
`RunAsync` behind the `WINHARNESS_FIXED_HEADER` opt-in env var. Guaranteed
teardown via `try/finally` + `Dispose`. Default chat path unchanged (no-op when
not opted in / redirected / too short). Header/footer content and streaming
routing are Phase 2-3. Resize handling is a Phase 4 stub.

## Validation

- Phase 0 spike `--verify` run: `ok=true`, exit 0. Result captured in
  `spike-result.json` and summarized in `findings.md`.
- Phase 1: 6 new `ScreenRegionControllerTests` (layout opt-in/redirect/
  minimum-height/region math + inactive-controller no-op). `dotnet build` clean
  (0 warnings, the lint gate). `dotnet test` full suite: 310 passed, 0 failed,
  0 skipped.

## Errors

None.
