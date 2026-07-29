# AGENTS.md

Guidance for contributors and agents working on the Trackdub public core.

## Project

Cross-platform, local-first AI dubbing engine, SDK, CLI, and pipeline.
.NET 10 / C# (LangVersion=latest). Apache-2.0 licensed.

**Strict dependency direction — Domain depends on nothing:**

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
Benchmarks → Application, Composition, Domain, Inference, Inference.Onnx, Infrastructure
Tools → Application, Domain, Infrastructure, Media
Contracts → Domain
Licensing → (nothing)
Analyzers → (nothing)
OnnxRuntime.Dnnl.Native → (nothing)
Domain → (nothing)
```

Implementation projects: `DubBench`, `Trackdub.Analyzers`, `Trackdub.Application`,
`Trackdub.Benchmarks`, `Trackdub.Cli`, `Trackdub.Composition`, `Trackdub.Contracts`,
`Trackdub.Domain`, `Trackdub.Inference`, `Trackdub.Inference.Onnx`,
`Trackdub.Infrastructure`, `Trackdub.Licensing`, `Trackdub.Media`,
`Trackdub.Media.Playback`, `Trackdub.Sdk`, `Trackdub.Tools`.
Native/package-only: `Trackdub.OnnxRuntime.Dnnl.Native`.
`Trackdub.Analyzers` is a Roslyn analyzer (`netstandard2.0`), compile-time only.

## Commands

```bash
# Build
dotnet build Trackdub.slnx -m:1

# Test (all)
dotnet test Trackdub.slnx -m:1

# Single test project
dotnet test tests/Trackdub.<Area>.Tests --no-restore -m:1

# Single test
dotnet test tests/Trackdub.Application.Tests --filter "FullyQualifiedName~<TestName>"

# CI build (Release, warnings as errors)
dotnet restore Trackdub.slnx
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build

# Run headless CLI
dotnet run --project src/Trackdub.Cli -- --help

# Solution filter builds
dotnet build Trackdub.Inference.slnx -m:1
dotnet build Trackdub.Sdk.slnx -m:1

# Benchmarks
dotnet run --project src/Trackdub.Benchmarks -- --help
```

## Configuration

- **NuGet.config** adds `dotnet-libraries` Azure Artifacts feed for `Microsoft.ML.Tokenizers` 3.x preview.
- **Package versions** centrally managed in `Directory.Packages.props`. Never add Version in `.csproj`.
- **Package lock files** via `RestorePackagesWithLockFile=true`.
- **ONNX DLL dedup** in `Directory.Build.targets`.
- Shared build props: `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.

## Coding Style

- File-scoped namespaces, `sealed` where extension not intended, `Async` suffix on async methods.
- Immutable `record` types in Domain.
- Don't suppress warnings casually. Fix or add targeted `<NoWarn>` with comment.
- Cross-platform is a requirement. Use portable .NET APIs by default.

## Testing

- xUnit, `dotnet test`
- Domain tests: fast, pure, no I/O.
- Application tests: fakes from `tests/Trackdub.TestDoubles/` (shared source via `<Compile Include>`).
- SDK tests: headless session factory + CLI integration surface; deterministic, offline.
- Infrastructure tests: may use temp SQLite.
- Pipeline changes: cover success, disabled/skipped, missing-prerequisite, and failure paths.
- Architecture tests: enforce dependency direction and structural invariants.

## Model Governance

- Bundled inventory: `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`
- Commercial licenses only. Unknown license = unsafe.
- Provider registered ≠ model downloaded ≠ stage ran ≠ stage succeeded.
- Don't add end-user runtime deps (Python, Conda, Docker, CUDA Toolkit).

## Commit Style

Imperative title: `Add ...`, `Remove ...`, `Fix ...`, `Revise ...`

## Key Rules

1. Domain depends on nothing. No inference code leaking upward.
2. Preserve original artifacts on skip/fail stages. Log exact skip reasons.
3. Prefer immutable execution snapshots for pipeline stages.
4. Cross-platform is a requirement. Don't assume any single OS.
5. Never fake readiness. Each state (registered, downloaded, ran, succeeded) is distinct.

## Linear (mandatory for agents)

Linear workspace [trackdubllc](https://linear.app/trackdubllc) (team **TS**) is the source of truth for trackable work across Trackdub repos.

Agents **must** both **reference and update** Linear autonomously:

1. Search for an existing `TS-*` / backlog id before creating duplicates.
2. Create issues for new work (bugs, follow-ups, debt) on the right project with `repo:*` / `area:*` / `agent-owned` labels.
3. Move issues In Progress → In Review → Done as work progresses; comment blockers and proof.
4. Mention `TS-xxx` / `Fixes TS-xxx` in PR bodies once GitHub↔Linear is connected.
5. Never mark Done without honest evidence. Never fake readiness in Linear either.

Full procedure: [docs/operations/linear-workflow.md](docs/operations/linear-workflow.md). Integrations (GitHub / Notion / Figma) are tracked under project **Platform & Tooling**.

## Repository Policy

See [docs/repository-policy.md](docs/repository-policy.md) for organization and governance details.


## Documentation

See [docs/index.md](docs/index.md) for the categorized documentation index (ADRs, architecture, specs, audits, operations, and more).