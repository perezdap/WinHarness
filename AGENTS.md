# WinHarness

A Windows-native, terminal-first AI coding harness (.NET 10) over OpenAI-compatible
endpoints and MCP tools. The single product is the `winharness` CLI
(`src/WinHarness.Cli`). There is no server, database, or other long-running service.

See `README.md` for the full command reference and `CONTEXT.md` for domain language.

## Cursor Cloud specific instructions

The Cloud VM is **Linux**, but the project targets `net10.0` (no OS suffix) and
restores/builds/tests/runs cross-platform. Only the *Native AOT `win-x64` publish*
and a handful of Windows-only runtime paths require Windows.

### Toolchain
- The .NET 10 SDK (`10.0.100`) is installed at `~/.dotnet` and added to `PATH` via
  `~/.bashrc`. If `dotnet` is not found in a non-login shell, use the full path
  `~/.dotnet/dotnet`.

### Build / lint / test / run (run from repo root)
- Restore: `dotnet restore WinHarness.sln`
- Build (this is the lint gate): `dotnet build WinHarness.sln -c Release --no-restore`
  - `Directory.Build.props` sets `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`,
    so a clean build *is* the enforced style/analyzer gate. CI only runs
    restore/build/test/publish (`.github/workflows/phase0-aot.yml`).
  - `dotnet format --verify-no-changes` reports pre-existing import-ordering and
    IL2026/IL3050 differences and exits non-zero. It is **not** part of CI and is
    **not** the lint gate; do not "fix" it by reformatting committed code.
- Test: `dotnet test WinHarness.sln -c Release --no-build`
  - 5 integration tests are **expected to skip** on non-Windows (ConPTY interactive
    executor, stdio MCP, Windows credential store, long-path service). All other
    tests must pass.

### Running the CLI on Linux
- Native AOT publish commands in `README.md`/CI target `win-x64` and **do not work on
  Linux** (cross-OS AOT is unsupported). On Linux the build emits a managed
  `linux-x64` dll instead.
- Run the built CLI with:
  `dotnet src/WinHarness.Cli/bin/Release/net10.0/linux-x64/winharness.dll <args>`
  (or `dotnet run --project src/WinHarness.Cli -- <args>`).
- Config lives at `~/.config/WinHarness` on Linux (the XDG fallback for `%APPDATA%`),
  not `%APPDATA%\WinHarness`.
- A no-network smoke flow that exercises core functionality:
  `version`, `diagnostics aot`, `config init`, `providers list`, and a built-in tool
  round-trip via `tools call` (`write_file` → `edit_file` → `read_file` → `grep` →
  `run_command`). On Linux use a real executable for `run_command` (e.g.
  `/bin/echo`) instead of the Windows `cmd.exe` example.
- `winharness chat` requires a configured + reachable model provider
  (e.g. a local Ollama at `http://localhost:11434/v1`); it is not needed for a
  basic smoke test.
