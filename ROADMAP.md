# WinHarness Roadmap

## Deferred from v0.1

### Anthropic-native provider

Anthropic-native support is intentionally deferred from v0.1.

Reason:

- Native AOT is a release gate for WinHarness.
- The official `Anthropic` package currently appears to have unresolved Native AOT support concerns.
- v0.1 focuses on OpenAI-compatible endpoints to keep the dependency graph smaller and more likely to publish with zero IL/trimming/AOT warnings.

Revisit when one of the following is true:

- the official Anthropic SDK publishes cleanly under Native AOT with zero warnings in WinHarness' representative code path,
- an ADR approves a source-generated REST adapter,
- an ADR approves another mitigation that preserves the single-file Native AOT release goal.

### Plugin provider implementation

`FuturePluginToolProvider` remains an architectural extension point only. Runtime plugin loading is deferred because arbitrary plugin loading conflicts with the v0.1 Native AOT and single-executable constraints.
