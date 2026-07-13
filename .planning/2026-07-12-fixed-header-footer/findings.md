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

## Technical direction
- Use DECSTBM scroll-region: `ESC[{top};{bottom}r`.
- Keep row 1+ as fixed header, last row(s) as fixed footer/input/status.
- Normal output must happen with cursor inside the scroll region. Fixed rows are repainted with absolute cursor moves.
- Restore with `ESC[r` (or `ESC[1;<height>r`) and clear/repaint on teardown.

## Risks / things to inspect before implementation
- `AssistantStreamWriter.TryEraseForReRender` row math uses full terminal height; with scroll region it should use the scroll-region height and be conservative when the block may scroll.
- `ThinkingIndicator` writes `\r` on current row; if current row is near footer or prompt it can corrupt fixed UI unless constrained.
- Spectre.Console may write cursor-control sequences; keep Spectre output inside the scroll region and avoid Spectre full-screen/live primitives initially.
- Window resize must be handled by polling `Console.WindowWidth/Height` before prompt, during steering loop, and possibly spinner tick.
- Redirected output must bypass fixed UI entirely.

## Manual test matrix
- Windows Terminal PowerShell
- Git Bash / MSYS pty if user uses it
- VS Code integrated terminal
- conhost.exe if relevant
- redirected input/output (`winharness ... > out.txt`) must remain clean
