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

Phase 0 complete (2026-07-13). DECSTBM verified against the real Windows
conhost screen buffer via a `--verify <path>` mode added to
`spikes/WinHarness.TerminalRegionSpike`. Result: fixed rows survive scroll,
region confines scrolling. Verdict: GO. No production (`src/`) code yet.

## Validation

- Phase 0 spike `--verify` run: `ok=true`, exit 0. Result captured in
  `spike-result.json` and summarized in `findings.md`.

## Errors

None.
