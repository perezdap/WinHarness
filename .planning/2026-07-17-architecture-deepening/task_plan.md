# Architecture deepening — three Strong candidates

Branch: `refactor/deepen-modules`. Source: architecture review 2026-07-17
(`%TEMP%\architecture-review-20260716-223837.html`). Vocabulary per
`.agents/skills/codebase-design` (module, interface, depth, seam, adapter,
leverage, locality); domain terms per `CONTEXT.md`.

## Scope (in)

Three Strong candidates, sequential commits, smallest first:

1. **Active-branch query module.** Nine hand-rolled parentId walks across six
   files (ChatSessionBootstrap, UsageFooter, SessionCompactionService,
   AutoCompactionService, ChatSession, SessionTreeChoices, SessionForkService,
   SlashCommandAdvanced import) get one home. Consumers stop walking the tree.
2. **One config mutation path.** Widen `ProviderConfigurator` until every
   front-end's mutation fits (set-defaults-with-repair as used by
   `providers use`/`models use`; the `/provider` slash repair policy routes
   through the same seam). Delete `ConfigFileUpdater` (unvalidated splice).
   LoginCommand consolidation is NOT in scope (that's card 6).
3. **One Turn event-consumption module.** Extract the AgentEvent consumption
   loop shared by `ExecuteTurnCoreAsync` (text), `RunJsonTurnAsync` (JSON),
   and `RpcHost.RunTurnAsync` into one module owning: artifact append, the
   Failed → Completed("partial") terminal protocol, usage extraction, and
   steering→follow-up promotion. Front-ends become presentation adapters.
   Behaviour parity for REPL/JSON; RPC gains the previously-drifted
   steering-promotion behaviour only if it fits the RPC contract cleanly —
   otherwise parity and a noted follow-up.

## Scope (out)

- Card 4 (slash-command registry), card 5 (consume-vs-cut capabilities),
  card 6 (OAuth login orchestration) — Worth-exploring cards, need a design
  decision pass first.
- Appendix items except where a card touches them directly.
- No behaviour changes visible to users except drift removal in card 3's
  consumers and (if clean) RPC steering promotion.

## Acceptance criteria

- `dotnet build WinHarness.sln -c Release` clean (TreatWarningsAsErrors gate).
- `dotnet test WinHarness.sln -c Release --no-build` green (5 non-Windows
  skips expected — this box is Windows, so all should run).
- New modules covered by unit tests through their interfaces (no
  InternalsVisibleTo additions, no reflection in tests).
- No public interface changes to IAgentRuntime's method signature; the
  terminal-event protocol becomes documented on the extracted module.
- CONTEXT.md updated if new domain terms are named.
