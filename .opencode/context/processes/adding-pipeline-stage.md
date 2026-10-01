# Process — adding a pipeline stage

Ordered, layered, gated. A new stage is a **COMPLEX** change by the orchestrator's classification: it touches Domain, Contracts, Application, and Composition, usually plus Inference/Inference.Onnx and the manifest.

Route to `@pipeline-inference` for the change; `@validation-gate` for the final gate; `@benchmark-perf` if any performance claim is attached.

## Step 0 — decide whether it belongs

A new stage is only warranted when the work has its own inputs, artifacts, and readiness semantics. Most requests are a new *variant*, a new *engine family*, or a new *tier* on an existing stage — those go in the manifest, not the pipeline. Confirm before writing code:

- new `RuntimeStage` + new stage handler, or
- existing stage, new execution path that needs its own artifacts and its own model.

## Step 1 — Domain: the name and the runtime identity

`src/Trackdub.Domain/StageRuns/StageNames.cs`

```csharp
public const string MyStage = "my-stage";     // kebab-case, stable string
```

`src/Trackdub.Domain/Common/RuntimePlanning.cs`

```csharp
public enum RuntimeStage
{
    // ...
    MyStage = 12,
}
```

Domain adds nothing else: no interfaces, no DI, no records that need a project reference. If you are reaching for a package or a type outside Domain, it belongs in Contracts.

**Verify:** the file compiles in isolation and `RuntimeStage` has no duplicate numeric values.

```bash
dotnet build src/Trackdub.Domain/Trackdub.Domain.csproj -m:1
```

## Step 2 — Contracts: the cross-boundary shape

`src/Trackdub.Contracts/` — only what crosses a project boundary.

- request/response records for the stage
- readiness projection if the stage adds a new gate (extend `ReadinessState` only when a genuinely new blocking condition exists — do not repurpose an existing state)
- any new interface the stage's collaborators are resolved through

Contracts may reference `Domain` and nothing else (ADR-0011). No implementation, no domain logic, no DI.

**Verify:**

```bash
dotnet build src/Trackdub.Contracts/Trackdub.Contracts.csproj -m:1
python tools/ci/verify-dependency-graph.py
```

## Step 3 — Application: the stage handler

Two shapes exist. Match the one that fits.

**Dubbing pipeline (SDK/CLI/headless):** stage metadata and order go in `src/Trackdub.Application/Dubbing/DubbingPipelineStages.cs` (`DefaultStageOrder` and/or `ExtendedStageOrder`, plus `PrerequisiteStages` / `StagesRequiringSourceMedia` / `StagesRequiringTargetLanguage` as appropriate). Execution lives in `DubbingPipelineEngine.cs`.

**Transcript generation pipeline (editorial shell):** implement `ITranscriptGenerationStage` (`string StageName { get; }`, `Task<TranscriptGenerationContext> ExecuteAsync(...)`) as a new file under `src/Trackdub.Application/Transcripts/Stages/`, and register it through `ITranscriptPipelineBuilder` (`TranscriptPipelineBuilder.cs`). Existing examples: `AsrGenerationStage`, `VadGenerationStage`, `SpeakerDiarizationStage`, `SpeechEnhancementGenerationStage`, `TextRefinementGenerationStage`, `SpeakerAssignmentAndPersistenceStage`.

Handler rules:

- `StageName` returns the `StageNames.*` value.
- Every `StageRunRecord.Start` call passes the constant, never a literal.
- Write artifacts through `IArtifactStore` write handles + `CommitAsync`.
- On skip or failure, **preserve prior artifacts** and write an explicit skip/failure reason (`StageSkipReasonCodes` if a code fits; a typed `TransientFailureKind` if it is transient).
- Accept `CancellationToken`; flow it to every await.
- No inference implementation, no raw SQL, no UI types, no `Microsoft.ML.OnnxRuntime` references.

**Verify:**

```bash
dotnet test tests/Trackdub.Application.Tests --no-restore -m:1
```

## Step 4 — Inference (only if the stage needs a model)

New *provider* or new *engine family*: abstraction in `src/Trackdub.Inference/Runtime/` (descriptors, requirements, ranking policy), concrete sessions in `src/Trackdub.Inference.Onnx/`. `Inference` never constructs a session.

Add to `AllowedProvidersByEngineFamily` for the stage if the default probe order does not fit, and say why in a comment referencing the constraint (e.g. graph cannot import).

**Verify:**

```bash
dotnet build Trackdub.Inference.slnx -m:1
dotnet test tests/Trackdub.Inference.Tests --no-restore -m:1
dotnet test tests/Trackdub.Inference.Onnx.Tests --no-restore -m:1
```

## Step 5 — Model manifest (only if a model is involved)

`src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json` plus `bundled-models.profiles.json` if capabilities/language coverage are shared. Required per entry: `task`, `engine_family`, `tier`, `license`, `commercial_allowed`, `redistribution_allowed`, `requires_attribution`, `requires_user_consent`, `voice_cloning`, `commercial_use_verified`, `revision`, `sha256`, `benchmark_entry`, `download_file_sources`, `download_file_hashes`, `variants`.

Rules: commercial license only, verified; unknown license is unsafe. Attribution obligations go to `THIRD_PARTY_NOTICES.md`.

**Verify:**

```bash
python tools/ci/validate-manifest-schema.py
python tools/ci/audit-bundled-model-manifest.py
python tools/ci/verify-manifest-hashes.py --structural --all-audited
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~ModelManifest" -m:1
```

## Step 6 — Composition: DI registration

`src/Trackdub.Composition/CompositionRoot.cs` (plus a focused registration file under `src/Trackdub.Composition/<Area>/` when the surface is large). Both hosts — desktop shell and CLI — must resolve the **same** registration. A stage that is constructed but not registered compiles and then fails at resolve time, which is the most expensive way to find out.

**Verify:** resolve the service in a test (Composition tests already build the root) and confirm the stage appears in the pipeline the test executes.

```bash
dotnet test tests/Trackdub.Composition.Tests --no-restore -m:1
```

## Step 7 — Tests: four paths, always

Pipeline tests must cover success, **disabled/skipped**, **missing-prerequisite**, and **failure**. Add:

- `tests/Trackdub.Domain.Tests/` — fast, pure, zero I/O (state machine, invariants)
- `tests/Trackdub.Application.Tests/` — fakes from `tests/Trackdub.TestDoubles/` (shared source via `<Compile Include>`), no real models
- `tests/Trackdub.Architecture.Tests/` — if you added a stage name, extend `KnownStageNameValues` in `StageNameConsistencyTests`

Also assert artifact preservation on skip/failure, and that the readiness state for the stage is the expected distinct value rather than a boolean.

**Verify:**

```bash
dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1
```

## Step 8 — Update the docs that are load-bearing

- `AGENTS.md` dependency diagram **only if** you added or changed a project reference — the architecture test compares it to the real `.csproj` files and will fail otherwise.
- An ADR under `docs/decisions/` when the change alters a boundary or a policy.
- `docs/architecture/` or `docs/specs/` when the stage is user-visible, per the taxonomy in `docs/repository-policy.md`.

Do not create a README unless asked.

## Step 9 — The final gate

Delegate to `@validation-gate`, or run `/validate` (which delegates to it). Full gate order, verbatim from `AGENTS.md`:

```bash
dotnet restore Trackdub.slnx -m:1
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
dotnet format Trackdub.slnx --verify-no-changes
```

Plus the non-build gates: dependency direction (`tools/ci/verify-dependency-graph.py` + `tests/Trackdub.Architecture.Tests`), `BannedSymbols.txt` (`Path.Combine` count in touched files), analyzer diagnostics, and `packages.lock.json` integrity. Full detail: `context/standards/validation-gates.md`.

If a step was narrowed to one test project, the report must say verbatim that the solution-wide Release gate was skipped and why.

## Step 10 — Execution proof

A green build is not a working stage. To claim the stage works, produce execution evidence via `@benchmark-perf` (`controlled-matrix` per-stage run, or the CLI stage runner):

```bash
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- controlled-matrix <fixture> --output <dir>
```

Then walk the readiness ladder rung by rung in the report: manifest entry → license/commercial decision → download + checksum → hardware provider → stage enabled → stage ran → output usable. Anything not proven is `NOT VERIFIED`.

## Verification checklist per step

| Step | Command | Pass signal |
|---|---|---|
| 1 Domain | `dotnet build src/Trackdub.Domain/Trackdub.Domain.csproj -m:1` | compiles; `StageNames` + `RuntimeStage` extended |
| 2 Contracts | `dotnet build src/Trackdub.Contracts/Trackdub.Contracts.csproj -m:1`; `python tools/ci/verify-dependency-graph.py` | compiles; graph clean |
| 3 Application | `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1` | four-path coverage green |
| 4 Inference | `dotnet build Trackdub.Inference.slnx -m:1` + both test projects | compiles and passes |
| 5 Manifest | 3 python validators + `ModelManifest` filter | zero errors |
| 6 Composition | `dotnet test tests/Trackdub.Composition.Tests --no-restore -m:1` | service resolves |
| 7 Tests | `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1` | stage name registered in the test's known set |
| 8 Docs | `dotnet format Trackdub.slnx --verify-no-changes` | no changes needed |
| 9 Gate | `/validate` full Release gate | `VALIDATION: PASS` |
| 10 Proof | `controlled-matrix` run | artifact exists, stage ran, timing recorded |

## Common mistakes

| Mistake | Symptom | Fix |
|---|---|---|
| **Leaking inference upward** | `Application`/`Domain`/`Contracts` referencing ONNX Runtime, `SessionOptions`, or a concrete provider type | abstractions in `Inference`, implementations in `Inference.Onnx`, wiring in `Composition`. `DomainHasNoProjectReferences` and `InferenceOnnxDoesNotImportApplicationContractsNamespace` catch the worst cases; the rest is review. |
| **UI types in Inference** | `Avalonia.*`, `ViewModel`, or view-model shapes referenced from `Inference`/`Inference.Onnx` | Inference returns DTOs. The shell binds to Contracts. |
| **Skipping DI registration** | compiles, tests pass with fakes, then `InvalidOperationException` at resolve time in the real host | register in `CompositionRoot` and assert resolution in a Composition test |
| **Declaring ready without execution proof** | "stage works" backed by a green build | require a `controlled-matrix`/CLI run artifact plus the readiness ladder walked rung by rung |
| **Inline stage-name literal** | `StageNameConsistencyTests` failure | use the `StageNames.*` constant |
| **Adding a constant but not the test's known list** | the new stage name is never caught as a literal | add it to `KnownStageNameValues` |
| **Collapsing readiness to a boolean** | "ready" meaning registered, or meaning files present | use the distinct `ReadinessState` values |
| **Reporting skip as success** | a resume counted as a speed sample or a pass | emit `EXISTING_ARTIFACTS_VALID` and say "skipped (resume)" |
| **Destroying artifacts on failure** | a failed stage wipes a usable prior artifact | preserve originals, record the reason code |
| **Hand-merging `packages.lock.json`** | conflict markers or a lock file no restore reproduces | take one side, then `dotnet restore Trackdub.slnx --force-evaluate -m:1` |
| **Adding a project reference without updating `AGENTS.md`** | `AgentsMdDiagramMatchesEveryCsprojProjectReference` failure | update both, same commit |
| **New `Path.Combine` call sites** | RS0030 warning count grows | use `Path.Join` |
| **Assuming the stage is registered in the runner** | the stage never executes in a headless run | add it to `DefaultStageOrder` / `ExtendedStageOrder` as appropriate |
| **Swapping the execution provider silently** | a "GPU" claim that ran on CPU | set `RequirePreferredExecutionProvider` for a hard pin, and report the EP actually used |
| **Skipping cross-platform legs** | works on Windows, fails on the CI Linux/macOS legs | portable .NET 10 APIs by default; multi-target only with a stated reason |