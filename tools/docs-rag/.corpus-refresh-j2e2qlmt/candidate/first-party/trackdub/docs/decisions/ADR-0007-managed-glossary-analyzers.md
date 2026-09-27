# ADR-0007: Managed glossary analyzers

- Status: Draft
- Date: 2026-05-07

## Context

Milestone 17 adds project glossary matching for languages where simple text scanning is not enough. The advanced analyzer pilot needs Japanese, Chinese, and Arabic tokenization without adding native runtimes, sidecars, Python, Java, GPL/LGPL dictionaries, or model manifest changes.

Lucene.NET provides managed analyzers for these languages, but the available analyzer packages are still `4.8.0-beta00017`. Their lockfile closure includes ICU4N alpha packages and older `Microsoft.Extensions.*` transitive dependencies. That is acceptable for a backend-only managed pilot, but it is a runtime dependency risk rather than just a license-notice item.

## Decision

Trackdub will use Lucene.NET managed analyzer packages only behind the Infrastructure glossary analyzer adapters:

- `Lucene.Net.Analysis.Kuromoji` for Japanese.
- `Lucene.Net.Analysis.SmartCn` for Chinese.
- `Lucene.Net.Analysis.Common` `ArabicAnalyzer` for Arabic.
- Korean and unsupported languages remain on the application-layer morphology-lite fallback matcher.

The package versions must stay centrally pinned in `Directory.Packages.props`. Do not add native tokenizer runtimes, Java, Python, MeCab, Nori sidecars, or GPL/LGPL dictionary binaries as part of this pilot.

Runtime guardrails:

- Infrastructure analyzer tests must instantiate the managed analyzers and tokenize representative Japanese, Chinese, and Arabic text.
- Composition tests must resolve `IGlossaryTermMatcher` through DI and exercise analyzer-backed tokenization to catch missing assemblies or loader mismatches.
- The Windows CI build runs the solution test suite in Release, so these smoke tests run against the main product dependency graph.

## Consequences

Positive:

- The analyzer layer improves glossary matching without changing Domain, Contracts, persistence schema, or UI.
- The implementation remains Windows-friendly and dependency-light compared with native or sidecar tokenizers.
- The matcher still has a deterministic morphology-lite fallback when an analyzer cannot produce usable spans.

Negative:

- Beta Lucene.NET and alpha ICU4N transitives may have runtime binding, trimming, or patch-cadence risk.
- Future package upgrades need focused analyzer and DI smoke validation, not only compile-time checks.
- This pilot does not provide full morphological coverage or target-language inflection.
