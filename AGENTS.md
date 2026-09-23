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

# Filter builds & benchmarks
dotnet build Trackdub.Inference.slnx -m:1
dotnet build Trackdub.Sdk.slnx -m:1
dotnet run --project src/Trackdub.Benchmarks.DevHost -- --help
```

## Coding Style & Testing
- Style: File-scoped namespaces, `sealed` where extension not intended, `Async` on async methods, immutable `record` in Domain.
- Treat warnings as errors (`TreatWarningsAsErrors=true`). Do not suppress casually.
- Domain tests: fast, pure, zero I/O.
- Application tests: fakes from `tests/Trackdub.TestDoubles/` (shared source via `<Compile Include>`).
- Pipeline tests: must cover success, disabled/skipped, missing-prerequisite, and failure paths.
- Commits: imperative titles (`Add ...`, `Fix ...`, `Remove ...`). Always use `git commit -m`.

## Model Governance
- Bundled inventory: `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`.
- Commercial license only. Unknown license = unsafe.
- Do not add end-user runtime dependencies (Python, Conda, Docker, CUDA Toolkit).
- Preserve original artifacts on skipped or failed stages; record explicit skip/failure reasons.
