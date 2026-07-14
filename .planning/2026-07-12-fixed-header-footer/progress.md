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

Phase 5 complete (2026-07-13). The hard env opt-in is replaced by a
capability probe: `WINHARNESS_FIXED_HEADER` is now an override (unset → trust
the probe, `1` → force on, `0` → force off). Added
`IAnsiConsoleConfigurator.IsVirtualTerminalEnabled`, implemented in
`WindowsAnsiConsoleConfigurator` (checks `ENABLE_VIRTUAL_TERMINAL_PROCESSING`
is set on the output handle via the existing `GetConsoleMode` P/Invoke;
non-Windows → `true`). `ScreenRegionController.Create(ansi)` resolves it via DI;
the pure three-state decision lives in `ResolveOptIn` (unit-tested). Feature is
now on by default in modern Windows Terminal / conhost and off when redirected
or on pre-Win10-Threshold terminals. Hardened teardown: `Enter` captures the
prior output encoding + sets an `_entered` flag; `Exit` keys off `_entered`
(not `IsActive`, so it restores even after `OnResize` deactivated mid-session),
resets the region via the parameter-less `ESC [ r` (robust when `Layout.Height`
is 0 post-shrink), restores cursor visibility + encoding; `Dispose` is
exception-safe (`IOException`/`PlatformNotSupportedException`) and idempotent.
Next: Phase 7 (AOT publish + verify) done below; Phase 6 (manual matrix on WT /
conhost / VS Code / redirected) remains for a human at real terminals.

### Earlier: Phase 7 complete (2026-07-13)

AOT publish verified. `dotnet publish src/WinHarness.Cli/WinHarness.Cli.csproj
-c Release -r win-x64 -p:PublishAot=true` → zero trimming/AOT warnings; publish
dir holds a single native `winharness.exe` (~25 MB), no managed DLLs (true
native AOT). Published-binary smoke checks pass: `--version` (0.3.0),
`diagnostics aot` (Native AOT configured), `diagnostics write`, `tools call`
round-trip (`write_file` → `read_file` → on-disk content matches), `run_command`
(`cmd.exe /c echo` stdout captured). The Phase 5 probe reuses the existing
`LibraryImport` P/Invoke — no AOT/trimming hazards introduced.

### Earlier: Phase 4 complete (2026-07-13)

The footer is now genuinely two rows: status on row `H-1` and the `›` prompt +
typed input on the last row `H` (outside the DECSTBM region), so typed input
never scrolls the conversation. Added `BeginPrompt`/`EndPrompt` (DECSC save →
position on row `H` → write `›` … clear row → DECRC restore) and a
`submitNewline` parameter on `ReadKeyLine` (active idle path passes `false`).
`ReadIdlePrompt` calls `OnResize` before repainting; `RunTurnWithSteeringAsync`
takes `screen` and calls `OnResize` per steering tick. Steering input stays
inline in the region during a turn.

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
- Phase 5: replaced env opt-in with capability probe (`IsVirtualTerminalEnabled`
  on `IAnsiConsoleConfigurator`; `ResolveOptIn` pure decision); hardened
  teardown (idempotent `_entered`-guarded `Enter`/`Exit`, encoding capture/restore,
  parameter-less `ESC [ r` reset, exception-safe `Dispose`); `Create(ansi)` wired
  via DI. +4 headless tests; build clean (0 warnings); suite 319 → 323 passed,
  0 failed, 0 skipped. Phase 0 spike re-run `ok=true`.
- Phase 7: `dotnet publish ... -r win-x64 -p:PublishAot=true` → zero trimming/AOT
  warnings; single native `winharness.exe` (~25 MB, no managed DLLs). Published
  binary verified: `--version`, `diagnostics aot` + `diagnostics write`, `tools
  call` round-trip (`write_file`/`read_file`), `run_command`. No AOT hazards
  introduced by the Phase 5 changes.

## Errors

None.
