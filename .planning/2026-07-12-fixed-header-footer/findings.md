# Findings: true fixed header/footer

## Current shipped state (commit d2431be)

- `ReadIdlePrompt(WinHarnessOptions, ChatSession)` prints `StatusLineFormatter.FormatMarkup(session, options)` immediately above `›`.
- `UsageFooter.Format` includes live `effort <value>`.
- `StatusLineFormatter.ResolveEffort`: session override > model configured default > `default`.
- Startup `WriteBanner` no longer prints the stale provider/model/effort line.

## Relevant code paths

- `Program.cs` `RunReplAsync`: main loop, reads idle prompt, executes slash commands, runs turns, prints per-turn usage footer.
- `Program.cs` `RunTurnWithSteeringAsync`: launches turn on background task, polls `Console.KeyAvailable` for steering / abort / follow-up while the model streams.
- `Program.cs` `ReadIdlePrompt` and `ReadKeyLine`: blocking key loop for idle input.
- `Program.cs` `TryReadSteeringInput`: nonblocking key drain during active turns.
- `Rendering/AssistantStreamWriter.cs`: streams raw assistant tokens, tracks row count, may erase and re-render markdown. Uses `Console.WindowWidth/Height` and raw cursor up / clear-to-end sequences.
- `Rendering/ThinkingIndicator.cs`: background spinner line with `\r`, erase/rewrite behavior.

## Phase 0 results (2026-07-13)

Ran the spike under a real Windows conhost console (forced 80×24), scrolling
60 lines through a 20-row DECSTBM region (rows 3..22), then read the actual
screen buffer back with `ReadConsoleOutputW` (added a `--verify <path>` mode
to `spikes/WinHarness.TerminalRegionSpike`).

Result file `spike-result.json` (committed alongside):

```json
{"ok":true,"error":null,"width":80,"height":24,"rows":[
  "[spike] FIXED HEADER - must never scroll away",
  "",
  "scrolling content line 042",
  ... (rows 3..22 = lines 042..060, the last 20 of 60) ...,
  "",
  "",
  "[spike] FIXED FOOTER - must never scroll away"
]}
```

- Header row 1: intact. Footer row 24: intact. Region rows 3..22 hold the
  last 20 of 60 scrolled lines (first 40 scrolled off the top).
- Header text never leaked into the region → DECSTBM scroll confined to the
  region as specified.
- `ok: true`. Exit code 0.

### Verdict: GO. DECSTBM works on the target Windows terminal

### Caveats / follow-ups for later phases

1. **Non-ASCII glyph transliteration.** The em-dash `—` in source became `-`
   in the screen buffer. `Console.WriteLine` writes through the console output
   codepage; raw `Console.Write` of non-ASCII (Spectre markup, box-drawing,
   emoji) may need `Console.OutputEncoding = Encoding.UTF8` or explicit UTF-8
   byte writes. Phase 3 audit must check this for header/footer content and
   `MarkdownConsoleRenderer`.
2. The spike does **not** exercise Spectre, resize, the steering poll loop,
   or the assistant stream writer — the real residual risk lives there
   (Phase 3–5), not in DECSTBM itself.
3. The `--verify` mode reuses `GetStdHandle(STD_OUTPUT_HANDLE)` +
   `SetConsoleWindowInfo`/`SetConsoleScreenBufferSize` + `ReadConsoleOutputW`;
   this self-verify pattern could become a headless test for the eventual
   `ScreenRegionController`.
4. Markdown line-length (MD013) is disabled repo-wide via `.editorconfig`
   (`[*.md] max_line_length = off`); the repo has never enforced 80-col
   markdown, so MD013 was firing as noise.

## Technical direction

- Use DECSTBM scroll-region: `ESC[{top};{bottom}r`.
- Keep row 1+ as fixed header, last row(s) as fixed footer/input/status.
- Normal output must happen with cursor inside the scroll region. Fixed
  rows are repainted with absolute cursor moves.
- Restore with `ESC[r` (or `ESC[1;<height>r`) and clear/repaint on teardown.

## Risks / things to inspect before implementation

- `AssistantStreamWriter.TryEraseForReRender` row math uses full terminal
  height; with scroll region it should use the scroll-region height and be
  conservative when the block may scroll.
- `ThinkingIndicator` writes `\r` on current row; if current row is near
  footer or prompt it can corrupt fixed UI unless constrained.
- Spectre.Console may write cursor-control sequences; keep Spectre output
  inside the scroll region and avoid Spectre full-screen/live primitives
  initially.
- Window resize must be handled by polling `Console.WindowWidth/Height`
  before prompt, during steering loop, and possibly spinner tick.
- Redirected output must bypass fixed UI entirely.

## Manual test matrix

- Windows Terminal PowerShell
- Git Bash / MSYS pty if user uses it
- VS Code integrated terminal
- conhost.exe if relevant
- redirected input/output (`winharness ... > out.txt`) must remain clean
