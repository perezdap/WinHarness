# Task: True fixed (sticky) header + footer in `winharness chat`

## Goal

Pin a header row at the top and a status/footer row at the bottom of the
terminal so they stay on screen while conversation output scrolls between them.
This supersedes the pragmatic "status line above the prompt" already shipped
(commit d2431be) with a genuinely fixed region.

## Motivation / follow-up context

The shipped change re-renders a compact status line above the prompt each turn
(good enough, always accurate) but it still scrolls away with history. The user
wants a real fixed header/footer. This requires taking control of the terminal
scroll region.

## Approach (chosen): DECSTBM scroll-region, stay in main buffer

- Use VT sequence `ESC [ <top> ; <bottom> r` (DECSTBM) to constrain the
  scrolling region to rows `top..bottom`, leaving row 1 (header) and the last
  1-2 rows (footer/prompt) fixed.
- Repaint header/footer with cursor-save/restore (`ESC 7` / `ESC 8`) or explicit
  `ESC [ row ; col H` positioning, never letting normal writes touch those rows.
- All existing streaming writes (AssistantStreamWriter, ThinkingIndicator, tool
  batch rendering, Spectre markup) must emit *inside* the scroll region only.

### Why not the alternate options

- Alt-screen buffer (`ESC [ ?1049h`): would lose native scrollback — the user
  scrolls history a lot in a REPL; rejected.
- Full Spectre `Live`/`Layout`: fights our custom `Console.ReadKey` input loop
  and streaming token writer; high risk, large rewrite; rejected for now.

## Key risks (must be validated)

1. ConPTY / Windows Terminal DECSTBM support + interaction with Spectre.
2. Resize handling: `Console.WindowWidth/Height` changes mid-session must
   recompute the region and repaint. No `SIGWINCH` on Windows — poll size.
3. The blocking `ReadKey`/`ReadKeyLine` idle loop and `RunTurnWithSteeringAsync`
   poll loop both write to the console; both must respect the region + repaint
   footer on each keystroke (cursor is inside region, footer is outside).
4. Cursor math: `AssistantStreamWriter.TryEraseForReRender` already does row
   math against `Console.WindowHeight`; must be reconciled with a shrunk region.
5. Scrollback pollution: DECSTBM does NOT protect fixed rows from being
   overwritten by absolute-positioned writes elsewhere; must audit every raw
   `Console.Write("\x1b[...")` site.
6. Non-interactive / redirected output (`Console.IsOutputRedirected`) and
   one-shot `RunTurnAsync` path must be a no-op (no region, no repaint).
7. Emoji/wide-char + line-wrap accounting for footer width (already partially
   handled in ThinkingIndicator.Truncate).

## Phases

- [x] Phase 0 — Spike: prove DECSTBM works under Windows Terminal + ConPTY
      (tiny throwaway: set region, scroll filler, keep header/footer fixed,
      resize, restore on exit). Decide go/no-go before touching the REPL.
      Initial spike added at `spikes/WinHarness.TerminalRegionSpike`; run it in
      Windows Terminal/ConPTY and record results before Phase 1.
      **DONE 2026-07-13:** added `--verify <path>` mode using
      `ReadConsoleOutputW` to read the real conhost screen buffer after
      scrolling 60 lines through a 20-row region. `ok=true`; header & footer
      intact, region confined. See `findings.md`. **Verdict: GO.**
- [x] Phase 1 — Introduce a `ScreenRegionController` (enter/exit, set region,
      repaint header, repaint footer, handle resize). Interactive-only; no-op
      when output redirected. Guaranteed teardown (restore region + cursor) via
      try/finally around the whole REPL and on Ctrl+C/crash.
      **DONE 2026-07-13:** added `src/WinHarness.Cli/Rendering/ScreenRegionController.cs`
      + `ScreenRegionLayout` (pure layout decision, unit-tested). Lifecycle
      wired into `RunAsync` via `try/finally` with `Dispose()` teardown.
      **Opt-in** via `WINHARNESS_FIXED_HEADER` so the default chat path is
      unchanged until content (Phase 2) + streaming (Phase 3) land; when active
      the controller owns the top row and the scrolling `WriteBanner` is
      suppressed. Resize (`OnResize`) is a Phase 4 stub. Tests: 6 new
      `ScreenRegionControllerTests` pass; full suite 310/0/0.
- [x] Phase 2 — Header: move startup banner content into the fixed top row(s);
      keep it concise (one line). Footer: render the live status line
      (StatusLineFormatter) + usage footer in the fixed bottom row(s).
      **DONE 2026-07-13:** added `ScreenHeaderFormatter` (one row:
      `WinHarness chat · provider · model · effort`) and `ScreenFooterFormatter`
      (status: `md on/off · context · tools`) in `WinHarness.Cli.Chat`.
      `ScreenRegionLayout` gained `Width` + `MinimumWidth`; `SetHeader`/`SetFooter`
      truncate to width. `WriteFixed` now saves/restores the cursor
      (`ESC 7`/`ESC 8` = DECSC/DECRC) so painting the fixed rows never moves the
      conversation cursor. Wired into `RunReplAsync`/`ReadIdlePrompt`: header +
      footer refresh every idle prompt (so `/model`, `/effort`, `/md`, tool-filter
      changes are reflected), and the in-region status line is skipped when active.
      Per-turn `UsageFooter` still renders in the scroll stream. The `›` prompt
      stays in the region (Phase 4 moves it into the footer). Tests: 5 new
      formatter tests + width/narrow-layout test; full suite 315/0/0.
- [ ] Phase 3 — Route all streaming/tool/markdown output through the scroll
      region; audit AssistantStreamWriter + ThinkingIndicator cursor math.
- [ ] Phase 4 — Input loops (ReadKeyLine idle + steering poll) repaint footer
      and keep the cursor inside the region; handle resize repaint.
- [ ] Phase 5 — Robust teardown + fallbacks: restore terminal on exit, abort,
      exceptions; auto-disable region on unsupported terminals (from Phase 0
      capability probe) and fall back to shipped behavior.
- [ ] Phase 6 — Tests: unit-test region math + formatters (headless); manual
      test matrix on Windows Terminal, conhost, VS Code terminal, redirected.
- [ ] Phase 7 — Build (Release, warnings-as-errors) + full test suite + publish
      AOT to PATH for manual verification.

## Decisions

- Stay in main screen buffer (preserve scrollback); use DECSTBM, not alt-screen.
- Interactive-only feature; redirected/one-shot paths unchanged.
- If Phase 0 shows DECSTBM is unreliable on target terminals, STOP and reassess
  (candidate fallback: keep shipped status-line behavior, or reconsider Spectre
  Live). Do not force a fragile solution.

## Open questions for the user (raise before Phase 1 if unsure)

- Header content: just `WinHarness chat | provider · model · effort`? Or include
  session name / context files / tool filter too (multi-row header)?
- Footer content: live status + usage in one row, or a two-row footer (status
  row + input prompt row)?
- Acceptable to drop the feature (fall back) on terminals without DECSTBM?

## Files (anticipated)

- new: `src/WinHarness.Cli/Rendering/ScreenRegionController.cs`
- `src/WinHarness.Cli/Program.cs` (RunReplAsync, WriteBanner, ReadIdlePrompt,
  RunTurnWithSteeringAsync teardown)
- `src/WinHarness.Cli/Rendering/AssistantStreamWriter.cs` (region-aware row math)
- `src/WinHarness.Cli/Rendering/ThinkingIndicator.cs` (region-aware)
- `src/WinHarness.Cli/Chat/StatusLineFormatter.cs` / `UsageFooter.cs` (reuse)
- new tests under `tests/WinHarness.IntegrationTests/`
