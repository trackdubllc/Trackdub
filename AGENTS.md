# AGENTS.md

Guidance for contributors and agents working on the Trackdub public core (`trackdubllc/Trackdub`).

## Core Principles
1. Strict dependency direction: **Domain depends on nothing**. No inference leaking upward.
2. Conflict order: source code/tests > task instructions > Linear > documentation.
3. **Never fake readiness:** Provider registered != model downloaded != stage ran != stage succeeded.
4. Cross-platform is required. Portable .NET 10 APIs by default. Extended operations: `docs/operations/cloud-operations.md`.
5. Linear (workspace `trackdubllc`, team **TS**): track work autonomously (`repo:core`). Never mark Done without proof.

## Dependency Architecture
```
Application → Contracts, Domain, Licensing
Infrastructure → Application, Contracts, Domain
Media → Application, Analyzers, Contracts, Domain
Media.Playback → Application, Domain
Inference → Contracts, Domain
Inference.Onnx → Inference, Contracts, Domain
Composition → Application, Inference, Inference.Onnx, Infrastructure, Licensing, Media, Media.Playback
Sdk → Application, Composition, Licensing
Cli → Sdk
DubBench → Benchmarks, Domain, Inference, Inference.Onnx
DubBench.DevHost → DubBench, Infrastructure
Benchmarks → Application, Composition, Domain, Inference, Inference.Onnx, Infrastructure
Benchmarks.DevHost → Benchmarks
Benchmarks.Micro → Inference.Onnx
Tools → Application, Domain, Infrastructure, Media
Contracts → Domain
Licensing → (nothing)
Analyzers → (nothing)
OnnxRuntime.Dnnl.Native → (nothing)
Domain → (nothing)
```

## Commands
```bash
# Build & Test
dotnet build Trackdub.slnx -m:1
dotnet test Trackdub.slnx -m:1

# Single test project or filter
dotnet test tests/Trackdub.<Area>.Tests --no-restore -m:1
dotnet test tests/Trackdub.Application.Tests --filter "FullyQualifiedName~<TestName>" -m:1

# CI validation (Release, warnings as errors)
dotnet restore Trackdub.slnx -m:1
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1

# Headless CLI
dotnet run --project src/Trackdub.Cli -- --help
dotnet run --project src/Trackdub.Cli --framework net10.0 -- --help   # Windows (multi-targeted)
```

## packages.lock.json conflicts
Don't hand-resolve merge conflicts in `packages.lock.json`. Take either side (`git checkout --ours` or `--theirs`), then regenerate:
```bash
dotnet restore Trackdub.slnx --force-evaluate -m:1

# Filter builds & benchmarks
dotnet build Trackdub.Inference.slnx -m:1
dotnet build Trackdub.Sdk.slnx -m:1
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- --help
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- controlled-matrix <fixture> --output <dir>
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- --list flat
```

## BenchmarkDotNet policy
- `src/Trackdub.Benchmarks.Micro` is the BenchmarkDotNet project for pure CPU, tensor, tokenizer, and opt-in real-model ONNX measurements.
- Keep `src/Trackdub.Benchmarks` as the source of truth for controlled end-to-end pipeline evidence and `BenchmarkEvidenceReport`; do not replace or merge it with BDN artifacts.
- Use `controlled-matrix` for comparable per-stage runs; it executes the controlled pipeline path and preserves each stage's evidence separately.
- Run BDN in Release mode with deterministic inputs and `GlobalSetup`; keep model, tokenizer, file, and session initialization outside measured methods.
- Do not run BDN on pull-request CI. CPU runs are manually/nightly triggered; real-model ONNX and saved-commit comparisons are opt-in through `.github/workflows/benchmark-dotnet.yml`.
- Use `scripts/ci/run_benchmarkdotnet_baseline.py` for saved-commit comparisons; compare like-for-like benchmark names and keep threshold results separate from correctness tests.
- BenchmarkDotNet usage and environment variables are documented in `docs/benchmarks/benchmarkdotnet.md` and `src/Trackdub.Benchmarks.Micro/README.md`.

## Coding Style & Testing
- Style: File-scoped namespaces, `sealed` where extension not intended, `Async` on async methods, immutable `record` in Domain.
- Treat warnings as errors (`TreatWarningsAsErrors=true`). Do not suppress casually.
- Prefer `Path.Join` over `Path.Combine` for new/changed code. `Path.Combine` silently drops
  earlier segments if a later one is rooted; CodeQL/CodeFactor flag it whenever that can't be
  proven false, which in practice is almost every call. `Path.Join` has no such reset behavior
  and is a drop-in replacement everywhere in this codebase. Enforced repo-wide as a build
  warning via `Microsoft.CodeAnalysis.BannedApiAnalyzers` (see `BannedSymbols.txt`,
  `Directory.Build.props`) — not an error yet since ~337 existing files still use `Path.Combine`.
- Domain tests: fast, pure, zero I/O.
- Application tests: fakes from `tests/Trackdub.TestDoubles/` (shared source via `<Compile Include>`).
- Pipeline tests: must cover success, disabled/skipped, missing-prerequisite, and failure paths.
- Commits: imperative titles (`Add ...`, `Fix ...`, `Remove ...`). Always use `git commit -m`.

## Model Governance
- Bundled inventory: `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`.
- Commercial license only. Unknown license = unsafe.
- Do not add end-user runtime dependencies (Python, Conda, Docker, CUDA Toolkit).
- Preserve original artifacts on skipped or failed stages; record explicit skip/failure reasons.
