# ADR-0001: Use ConsoleAppFramework for command-line parsing

## Status

Accepted

## Context

WinHarness must publish as a single Native AOT executable. Command-line parsing therefore must avoid runtime reflection, dynamic code generation, and APIs marked as incompatible with trimming or AOT.

`Spectre.Console` is used for terminal rendering only. `Spectre.Console.Cli` is explicitly out of scope because its command and settings model relies on reflection-heavy discovery and is not an AOT-safe choice for this application.

The two acceptable parser candidates were:

- ConsoleAppFramework
- System.CommandLine

## Decision

Use ConsoleAppFramework for the `Cli/` layer.

## Rationale

ConsoleAppFramework v5 is source-generator based and advertises a zero-reflection, Native AOT-safe command model. This aligns directly with the WinHarness cold-start and single-executable goals.

System.CommandLine is viable, but ConsoleAppFramework has the simpler AOT story for this project because the command surface can be statically generated without reflection-based command binding.

## Consequences

- CLI commands must be explicitly registered.
- Command handlers should use source-generator-friendly signatures.
- No reflection-based command discovery is allowed.
- Spectre.Console remains limited to rendering.
