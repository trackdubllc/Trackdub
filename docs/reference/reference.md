# Implementation Plan — G3: Cloud Egress Visibility & Consent

**Source:** [design-g3-cloud-egress-visibility.md](design-g3-cloud-egress-visibility.md)

**Prerequisite:** G5 Phase 1–2 (Contracts + Application Evaluate + Panel) must land first. G3 builds on ReadinessState, IPipelineReadinessService, and the readiness panel.

---

## Phase 1: Contracts — Cloud consent model (1 day)

Add consent tracking + exceptions.

**Files:**
- src/Trackdub.Contracts/Cloud/EgressType.cs
- src/Trackdub.Contracts/Cloud/CloudEgressDescription.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentKeys.cs
- src/Trackdub.Contracts/Cloud/CloudEgressConsentException.cs

**Logic:**
- EgressType enum: Audio, Text, Media
- CloudEgressDescription record: ConsentKey, EgressType, ProviderName, DataDescription, Endpoint, PrivacyPolicyUrl
- CloudEgressConsentKeys static: constants for all 8 consent keys + Build(type, providerKey)
- CloudEgressConsentException: throw when engine lacks consent

---

## Phase 2: Application — Consent service (2 days)

Track and query consent state.

**Files:**
- src/Trackdub.Contracts/Cloud/ICloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentService.cs
- src/Trackdub.Application/Cloud/CloudEgressConsentCatalog.cs (static)

**Logic:**
- ICloudEgressConsentService: HasConsent(key), GetRequiredConsentKeys(stage, alias), SetConsentAsync(key, consented, ct)
- Impl reads/writes StudioSettings.CloudEgressConsents dict
- Catalog: static 8-entry table (audio:openai, audio:gemini, text:deepl, text:openai, text:gemini, text:elevenlabs, text:google, media:elevenlabs)

---

## Phase 3: Composition — Registration (1 day)

Wire consent service into app + SDK.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register ICloudEgressConsentService → CloudEgressConsentService
- Inject into IPipelineReadinessService (for consent probes)
- Inject into cloud engine instances (ElevenLabsCloudTtsEngine, OpenAiCloudTranscriptionEngine, etc.)

---

## Phase 4: Application — Readiness integration (2 days)

Extend G5's readiness to check consent.

**Files:**
- (extend) src/Trackdub.Application/Pipeline/PipelineReadinessService.cs
- (extend) src/Trackdub.Contracts/Pipeline/ReadinessState.cs (add CloudEgressConsentRequired)

**Logic:**
- EvaluateAsync: for each cloud alias, check consent via ICloudEgressConsentService.HasConsent(key)
- If missing consent, return CloudEgressConsentRequired state
- G5's readiness panel already renders per-stage badges; consent badge + resolve action follow existing pattern

---

## Phase 5: App — Consent dialog (2 days)

Proactive consent prompt on model selection + pre-run backstop.

**Files:**
- src/Trackdub.App.Avalonia/Views/CloudEgressConsentDialog.axaml
- src/Trackdub.App.Avalonia/ViewModels/CloudEgressConsentViewModel.cs

**Logic:**
- Show when cloud alias selected (post-selection, proactive)
- Display egress type (Audio 🎙 / Text 📝 / Media 🎬), provider, data description, endpoint, privacy link
- [Allow] [Not now] buttons
- On Allow: SetConsentAsync(key, true) → persist to settings
- On Not now: keep selection, but stage blocked at pre-run (backstop catches it)

---

## Phase 6: Infrastructure — Defense-in-depth (1 day)

Assert guards in cloud engines.

**Files:**
- (extend) src/Trackdub.Infrastructure/Tts/ElevenLabsCloudTtsEngine.cs
- (extend) src/Trackdub.Infrastructure/Transcription/OpenAiCloudTranscriptionEngine.cs
- (extend) src/Trackdub.Infrastructure/Translation/DeepLCloudTranslationEngine.cs
- (extend) src/Trackdub.Infrastructure/Dubbing/ElevenLabsCloudDubbingEngine.cs

**Logic:**
- Each engine: at top of Synthesize/Translate/Transcribe/Dub, assert HasConsent(consentKey)
- Throw CloudEgressConsentException if absent (safety net; gate prevents this path)

---

## Tests

- HasConsent returns false for absent key; true for explicit true in dict
- GetRequiredConsentKeys returns correct key(s) for each cloud alias
- EvaluateAsync returns CloudEgressConsentRequired when consent absent
- audio:openai and text:openai are separate (both required for OpenAI ASR + TTS pair)
- ConsentDialog persists consent to StudioSettings
- Engine assert throws CloudEgressConsentException when consent absent

# Implementation Plan — G4: Run Progress & ETA

**Source:** [design-g4-run-progress-eta.md](design-g4-run-progress-eta.md)

---

## Phase 1: Contracts — Progress model (1 day)

Add per-stage progress record.

**Files:**
- src/Trackdub.Contracts/Pipeline/StageProgressReport.cs

**Logic:**
- StageProgressReport: StageName, PercentComplete?, ItemsComplete, TotalItems?, EstimatedTimeRemaining?, DisplayLabel
- Immutable record

---

## Phase 2: Application — ETA + Context (2 days)

Throughput tracker + extend context.

**Files:**
- src/Trackdub.Application/Pipeline/StageThroughputTracker.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/TranscriptGenerationContext.cs

**Logic:**
- StageThroughputTracker: Report(itemsComplete, totalItems) → TimeSpan? ETA
  - Simple ms/item avg; suppress before 200ms elapsed
- TranscriptGenerationContext: add IProgress<StageProgressReport>? StageProgress field (optional, backward-compatible)

---

## Phase 3: Application — Stage progress wiring (3 days)

Add per-segment/region progress to handlers.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationOrchestrationService.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs

**Logic:**
- TTS handler: loop over segments, report per segment
- Translation service: loop over segments, report per segment
- ASR handler: loop over regions, report per region (use StageThroughputTracker)
- All use StageNames constant for StageName field

---

## Phase 4: SDK — Progress bridging (2 days)

Connect stage progress to pipeline events.

**Files:**
- (extend) src/Trackdub.Sdk/TrackdubDubbingEngine.cs

**Logic:**
- StageProgressAdapter: convert StageProgressReport → PipelineProgressEvent(kind=Progress)
- Thread IProgress<StageProgressReport> into TranscriptGenerationContext
- Download bridge (temporary until G5 lands): wrap ModelDownloadProgress → PipelineProgressEvent(Progress)
- Black-box stages (VAD/Diar/Sep): emit Progress event + optional periodic heartbeat

---

## Phase 5: App — VM progress fields (2 days)

Add progress bindings to view models.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineStageRowViewModel.cs
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineRunViewModel.cs

**Logic:**
- PipelineStageRowViewModel: ProgressPercent, IsIndeterminate, EtaText fields
- PipelineRunViewModel: StagesComplete, StagesTotal, OverallElapsedText, OverallEtaText fields
- Subscribe to PipelineProgressEvent stream, update per-stage + overall on Progress kind

---

## Phase 6: App — AXAML bindings (2 days)

Add progress bars + ETA display to UI.

**Files:**
- (extend) src/Trackdub.App.Avalonia/Views/PipelineStagesView.axaml
- (extend) src/Trackdub.App.Avalonia/Views/RunConfigView.axaml

**Logic:**
- ProgressBar binds to PipelineStageRowViewModel.ProgressPercent
- IsIndeterminate binds to IsIndeterminate (for VAD/Diar/Sep)
- EtaText label shows "~23s remaining" or "00:01:42 elapsed"

---

## Phase 7: CLI — Progress rendering (1 day)

Update CliProgressReporter for Progress kind.

**Files:**
- (extend) src/Trackdub.Cli/CliProgressReporter.cs

**Logic:**
- Handle PipelineProgressEventKind.Progress
- Render as: [Stage   ] ████░░░░░░ 45%  12 / 26 segments  (~2m 15s)
- Black-box stages show "running…" with elapsed

---

## Tests

- StageThroughputTracker returns null before 200ms; finite ETA after
- StageProgressAdapter maps StageProgressReport correctly
- Translation emits N events for N segments
- TTS emits N events for N speakers' segments
- ASR emits M events for M regions
- VAD/Diar/Sep emit at least one Progress event (PercentComplete=null)
- PipelineRunViewModel.StagesComplete increments on Completed/Skipped

# Implementation Plan — G5: Consolidated Pipeline Readiness Gate

**Source:** [design-g5-readiness-gate.md](design-g5-readiness-gate.md)

---

## Phase 1: Contracts & core models (2 days)

Establish the read-only types and service interface. Map ReadinessState enum directly to Spec §5 (11 distinct states). Extend RunReadinessSnapshot with frozen RuntimeModelSelections.

**Files:**
- src/Trackdub.Contracts/Pipeline/ReadinessState.cs
- src/Trackdub.Contracts/Pipeline/StageReadiness.cs
- src/Trackdub.Contracts/Pipeline/IPipelineReadinessService.cs

---

## Phase 2: Application layer — Evaluate (3 days)

Build PipelineReadinessService. Evaluate per stage: artifact resumability, download/import/blocked status, cloud-key presence, voice-clone consent. Cache by (stage, selection-hash, artifact-fingerprint).

---

## Phase 3: Application layer — Provision (2 days)

Extend RuntimeModelSetupCoordinator. Batch DownloadRequired/ImportRequired stages by (ProviderKey, ModelId). Call RuntimeModelSetupWorkflow once. Demote per-stage Ensure* to non-interactive assert.

---

## Phase 4: SDK — pre-flight + Provision front-load (3 days)

Move provisioning fully up front in TrackdubDubbingEngine.RunPreFlightChecksAsync. Auto-download eligible stages. Fail fast with aggregated error listing all unmet stages. Delete stageProvisionedDuringExecution branch.

---

## Phase 5: App — readiness panel + live re-validation (4 days)

Build RunConfigPanelViewModel. Bind to draft selections. Debounce tier/lang/voice changes (300ms). Re-evaluate only affected stages (cache). Update per-stage badges live. Pre-run backstop: refuse Run while any stage is blocking.

---

## Phase 6: App — demote per-stage Ensure* (1 day)

Remove dialog calls from stage runners. Replace with non-interactive assert. Gate prevents reaching this assert path.

---

## Phase 7: Cleanup — diarization mismatch (1 day)

Verify SpeakerDiarizationStage calls CreateRuntimeSelections(snapshot), not CreateDefaultRuntimeSelections(). Both provision and execute see same snapshot.

---

## Risks

- Evaluate cost (disk + EP probes): mitigated by debounce + cache. Watch stale-cache if artifact store mutated outside context.
- SDK behavior change (longer pre-flight, fail-fast): confirm with CLI/API consumers.
- Cloud key validity: default to "present"; validate on explicit user action only.

# Implementation Plan — G7: Export Provenance & Attribution

**Source:** [design-g6-g7-attribution-provenance.md](design-g6-g7-attribution-provenance.md)

---

## Phase 1: Contracts + Domain (1 day)

Attribution types + extend StageRunRecord.

**Files:**
- src/Trackdub.Contracts/Artifacts/StageRunRecord.cs (extend)
- src/Trackdub.Contracts/Export/ExportManifestModel.cs (new)
- src/Trackdub.Contracts/Export/ExportAttributionRequirement.cs (new)

**Logic:**
- StageRunRecord: add ModelAlias? field (nullable, migration-safe)
- ExportManifestModel: Stage, ModelAlias, ModelId?, IsCloud, CloudProviderKey?, License?, RequiresAttribution
- ExportAttributionRequirement: ModelAlias, Stage, License, AttributionText?, SourceUrl?

---

## Phase 2: Application — Catalog (2 days)

Build attribution lookup from manifest.

**Files:**
- src/Trackdub.Application/Export/ModelAttributionCatalog.cs (static)
- src/Trackdub.Contracts/Export/IModelAttributionCatalog.cs

**Logic:**
- Catalog: keyed by model alias (lowercase), populated from bundled-models.manifest.json at compose time
- Find(alias) → ModelAttributionEntry?
- Cloud aliases map to RequiresAttribution=false, License="cloud-service-terms"

---

## Phase 3: Application — Manifest extension (2 days)

Extend ExportManifest + builder.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/ExportManifestModels.cs
- (extend) src/Trackdub.Application/Transcripts/ExportStageHandler.cs

**Logic:**
- ExportManifest: add ContributingModels: IReadOnlyList<ExportManifestModel>, AttributionRequired: IReadOnlyList<ExportAttributionRequirement>
- ExportManifestBuilder.Build(): call BuildContributingModels(request, catalog); filter RequiresAttribution=true → AttributionRequired

---

## Phase 4: Composition — Registration (1 day)

Wire catalog into app.

**Files:**
- src/Trackdub.Composition/CompositionRoot.cs

**Logic:**
- Register IModelAttributionCatalog → ModelAttributionCatalog (populated from manifest at startup)
- Inject into ExportManifestBuilder

---

## Phase 5: Infrastructure — Stage run persistence (1 day)

SQLite migration + write ModelAlias.

**Files:**
- (SQLite migration script)
- (extend) Stage run persistence code

**Logic:**
- Add ModelAlias column to stage_runs table (nullable)
- Extend stage run save to write ModelAlias from execution summary

---

## Phase 6: Composition — Stage handler wiring (1 day)

Pass ModelAlias on stage completion.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/SpeakerDiarizationStage.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationWorkflow.cs
- (extend) src/Trackdub.Application/Transcripts/TtsWorkflow.cs

**Logic:**
- Each stage handler: extract ModelAlias from StageRuntimeExecutionSummary
- Call StageRunHelper.CompleteAsync(..., modelAlias)

---

## Phase 7: App — Attribution surface (2 days)

Show in export success view.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/ExportMixViewModel.cs
- (extend) src/Trackdub.App.Avalonia/Views/ExportMixView.axaml

**Logic:**
- ExportMixViewModel: bind ContributingModels, AttributionRequired
- Show per-stage model summary if any cloud provider used
- Show AttributionRequired section only if HasAttributionRequired=true
- Display license + HF link per model

---

## Tests

- ExportManifestBuilder empty StageModels → ContributingModels has TTS only (backward compat)
- With StageModels: all stages reflected; dedup by (Stage, ModelAlias)
- AttributionRequired contains only RequiresAttribution=true entries
- Cloud alias → IsCloud=true, RequiresAttribution=false, CloudProviderKey set
- ModelAttributionCatalog.Find("kokoro-onnx") → RequiresAttribution=true, Apache-2.0
- ModelAttributionCatalog.Find("whisper-tiny-onnx") → RequiresAttribution=false
- Export no attribution models → AttributionRequired empty; UI section hidden
- SQLite migration: null ModelAlias → manifest omits entry (no crash)

# MIGraphX phase 0 — extension points

| Area | Location |
|------|----------|
| Provider enum | `src/Trackdub.Domain/Common/RuntimePlanning.cs` — `ExecutionProviderKind` |
| Milestone provider order | `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs` — `Milestone5PlanningPolicy`, `StageRuntimeRequirementsCatalog` |
| Discovery | `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderDiscovery.cs` |
| Bootstrap (platform) | `WindowsExecutionProviderBootstrapper`, `LinuxExecutionProviderBootstrapper` |
| WinML catalog | `WindowsMlExecutionProviderBootstrapper.Windows.cs`, `WindowsMlProviderRegistrationPolicy.cs` |
| Session options | `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs` — `CreateSessionOptions` |
| Smoke tests | `OnnxExecutionProviderSmokeTester.cs` |
| Devices | `WindowsDeviceEnumerator.cs`, `LinuxDeviceEnumerator.cs` |
| Studio hardware overrides | `HardwareOverrideCatalog.cs`, `IStudioSettingsService.HardwareOverrides` |
| DI | `CompositionRoot.AddInference` |
| Strategy doc | [ADR-0002-windows-ml-provider-strategy.md](../adr/ADR-0002-windows-ml-provider-strategy.md) |

# Olive recipe pilot

Trackdub can run [Olive](https://microsoft.github.io/Olive/) **recipe configs** for a small pilot set of bundled models instead of always using `olive auto-opt`.

## Scope

- **Automatic (Model Manager):** manifest `recipe_bindings` on pilot families `whisper-genai`, `whisper-onnx`, and `phi-genai`.
- **Fallback:** `auto-opt`, or bundled GenAI folder optimization when no recipe matches.
- **Developer override (ModelLab only):** `--olive-recipe-config <path>` runs `olive run --run-config <path>`.

End users still choose **execution provider** and **precision** only; there is no recipe picker in Model Manager.

## Recipes root

Recipe JSON files are **not vendored** in the repo. Set:

```powershell
$env:TRACKDUB_OLIVE_RECIPES_ROOT = "D:\Dev\olive-recipes"
```

Paths in `bundled-models.manifest.json` are relative to that root (for example `openai-whisper-tiny/cpu/whisper-tiny_cpu_int8.json`).

If the variable is unset or the config file is missing, optimization falls back to `auto-opt` and logs the reason in the optimization log.

## Manifest bindings

Optional `optimization.olive.recipe_bindings` entries:

| Field | Meaning |
|-------|---------|
| `provider` | `cpu`, `dml`, `cuda`, … (omit for any provider) |
| `precision` | `fp32`, `fp16`, `int8`, `int4`, … (omit for any precision) |
| `config_relative_path` | Path under `TRACKDUB_OLIVE_RECIPES_ROOT` |

Pilot models in the bundled manifest include Whisper GenAI (`openai/whisper-tiny`, `openai/whisper-base`), ONNX Whisper (`onnx-community/whisper-tiny`), and Phi GenAI (`microsoft/Phi-3.5-mini-instruct-onnx`).

## ModelLab override

```powershell
dotnet run --project src/Trackdub.Tools -- model-lab --olive-recipe-config "D:\Dev\olive-recipes\microsoft-Phi-3.5-mini-instruct\aitk\phi3_5_dml_config.json" ...
```

Unsupported in Model Manager UI; invalid override paths fail the run with an explicit error.

## Progress

Olive stdout lines matching `Step N/M` are also emitted as `[progress] Step N/M` for structured UI parsing; raw lines are still logged.

## Multi-component GenAI

When no recipe is selected and the model has multiple ONNX components in GenAI builder mode, optimization uses **shared bundled folder** `auto-opt` (`UseSharedComponentCache`) instead of per-component loops.

# Trackdub performance profiling report

> **Status:** DRAFT — scaffold (M20 PR4). Numbers marked *pending local run* are placeholders until measured on a reference machine.
> **Last updated:** 2026-06-13
> **Branch evidence:** `agent/cursor/m20-profiling-report`

## Measurement methodology (fill before claiming budgets)

Use the same procedure on every run so rows in this report stay comparable.

| Step | Tool / command | Record in report |
|---|---|---|
| Cold startup | `Stopwatch` from `Main` entry to first interactive shell frame, or ETW/`dotnet-trace` | ms, TFM, commit SHA |
| Working set | Task Manager **Private working set** or `dotnet-counters monitor --counters System.Runtime` | MB after 5 min idle |
| Export throughput | Wall clock around export command; note FFmpeg profile and segment count | duration, real-time factor |
| SQLite plans | `dotnet test tests/Trackdub.Infrastructure.Tests --filter FullyQualifiedName~Explain` | pass/fail + index names |
| UI layout | `Trackdub.UI.Tests` layout facts; PNG only when `CAPTURE_UI_SCREENSHOTS=1` | test name + optional PNG path |
| Inference / export bench | `dotnet run --project src/Trackdub.Benchmarks -- --help` then targeted scenario | log path, model manifest IDs, EP policy |

**Rules:** never collapse provider registered, model downloaded, stage ran, and stage succeeded. Label every number as *measured on reference machine* or *pending local run*. Do not copy example rows below into release notes as real data.

## Reference machine (fill before claiming budgets)

| Field | Value |
|---|---|
| OS | *pending local run* |
| CPU | *pending local run* |
| RAM | *pending local run* |
| GPU / EP | *pending local run* (Windows ML policy, DirectML fallback, etc.) |
| Trackdub commit | *pending local run* |
| TFM exercised | `net10.0-windows10.0.19041.0` (Windows) / `net10.0` (portable) |

## Startup (cold, ms)

Measured from process launch to first interactive shell frame (no project open).

| Scenario | Target (draft) | Measured | Notes |
|---|---:|---:|---|
| Avalonia shell cold start | TBD | *pending local run* | `dotnet run --project src/Trackdub.App.Avalonia -f net10.0-windows10.0.19041.0` |
| Shell + empty project open | TBD | *pending local run* | Includes SQLite migrate/open |
| Model Manager gate (bundled ONNX) | TBD | *pending local run* | Separate from shell; do not collapse readiness states |

### Example row format (illustrative only — not measured)

Replace these example values after a reference-machine run. They exist only to show how completed tables should read.

| Scenario | Target (draft) | Measured (example) | Notes |
|---|---:|---:|---|
| Avalonia shell cold start | 500 | 480 | example only |
| Idle shell, no media | 350 MB | 320 MB | example only |
| Audio mix export (5 min source) | RTF ≤ 1.0 | 0.85 | example only |

## Working set (MB)

Private bytes / working set after steady state (5 min idle, no pipeline run).

| Scenario | Target (draft) | Measured | Notes |
|---|---:|---:|---|
| Idle shell, no media | TBD | *pending local run* | Task Manager or `dotnet-counters` |
| Project open, transcript loaded | TBD | *pending local run* | Typical editor session |
| Post-ASR + translation (no TTS) | TBD | *pending local run* | Pipeline artifacts on disk; memory in-process |

## Export throughput

| Export profile | Media duration | Wall time | Real-time factor | Measured |
|---|---:|---:|---:|---|
| Audio mix (default) | *pending local run* | *pending local run* | *pending local run* | *pending local run* |
| Video mux (if applicable) | *pending local run* | *pending local run* | *pending local run* | *pending local run* |

**Method:** note FFmpeg/libmpv path, segment count, and whether `MatchOriginalLoudness` was enabled.

## SQLite query plans

Hot paths audited in `tests/Trackdub.Infrastructure.Tests/SqliteExplainQueryPlanTests.cs`:

| Query | Table / index expectation | CI audit |
|---|---|---|
| Glossary by project + language pair | `glossary_entries` / `ix_glossary_entries_project_language` | EXPLAIN asserts indexed `SEARCH` |
| Stage runs by project | `StageRuns` / `ix_stage_runs_project_id` | EXPLAIN asserts indexed `SEARCH` |
| Transcript segments by revision | `transcript_segments` / `ix_transcript_segments_revision_id` | EXPLAIN asserts indexed `SEARCH` |
| Glossary empty project (0 rows) | same glossary index | EXPLAIN still `SEARCH`; no rows seeded |
| Stage runs empty project (0 rows) | same stage-run index | EXPLAIN still `SEARCH`; no rows seeded |
| Transcript segments empty revision | same segment index | revision saved with 0 segments |
| Glossary large fixture (~400 rows) | same glossary index | indexed `SEARCH` under volume |
| Stage runs large fixture (~250 rows) | same stage-run index | indexed `SEARCH` under volume |
| Transcript segments large fixture (~1.2k rows) | same segment index | indexed `SEARCH` under volume |

**Notes:**

- Audit uses `EXPLAIN QUERY PLAN` on a migrated project DB with representative seed rows.
- EXPLAIN verifies indexed `SEARCH` plan shape; it does not assert wall-clock latency. Stale stats or poor selectivity can still make an indexed query slow — see follow-up item 6.
- Empty-table cases still assert indexed `SEARCH` for the hot-path predicates used in production queries.
- Full project-scale soak (10k+ segments) remains a follow-up measurement pass, not a CI budget gate yet.

## Profiler / benchmark history

| Source | Location | Status |
|---|---|---|
| DubBench / `Trackdub.Benchmarks` harness | `src/Trackdub.Benchmarks` | *pending local run* — capture baseline JSON or log excerpt |
| Inference session pool tests | `tests/` (session pooling) | present in repo; link results in follow-up |
| User benchmark SQLite (`BenchmarkRuns` table) | per-user DB | wired on `main` via M19; link results in follow-up |
| Hardware profiler history recorder | `src/Trackdub.Composition/HardwareProfiler` | present on `main`; capture history path in follow-up |

Record commit hash, model manifest IDs, and EP selection policy (`WindowsMlExecutionDevicePolicy`) with every benchmark run.

### TensorRT RTX EP ABI plugin (Windows NVIDIA)

| Field | Value |
|---|---|
| Model id | *pending local run* (`onnx-community/silero-vad` suggested) |
| Plugin version | `0.3.0/cu12` |
| Command | `Trackdub.Benchmarks --provider trt-rtx --runs 1 --format console` |
| Headless probe | `trackdub providers trt-rtx status` |
| Wall time (ms) | *pending local run* |
| Actual EP reported | *pending local run* (`NvTensorRTRTXExecutionProvider`) |
| Commit SHA | *pending local run* |
| Plugin dir | `%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64` or `TRACKDUB_TRT_RTX_EP_DIR` |

## Avalonia UI / render budget (headless)

| Check | Test class | Evidence |
|---|---|---|
| Component layout + optional PNG | `ComponentScreenshotTests` | `CAPTURE_UI_SCREENSHOTS=1` → `.design/.../headless/components/` |
| Shell panel state | `ShellTests` | layout/state assertions (no PNG) |
| Main window / side panel / transport | `*LayoutTests` | bounds and alignment |
| Glossary panel chrome | `ComponentScreenshotTests.Glossary_panel_*` | expanded/collapsed layout |

Long waveform / timeline frame budget: *pending local run* (needs media fixture + scrub profile).

## Load snap budget (progressive import/open)

Progressive load emits structured snap events to `%LOCALAPPDATA%\Trackdub\trackdub.log` with the `Snap.` prefix (see `SnapBudgetLog` in `Trackdub.App.Avalonia`).

| Event | When |
|---|---|
| `Snap.Import.Start` | Import entry |
| `Snap.Spine.Ready` | After `CreateMediaSpineAsync` |
| `Snap.Shell.Bound` | After first shell apply with `reopenPlayback: false` |
| `Snap.Normalize.Start` / `Snap.Normalize.Ready` | Background normalize job |
| `Snap.Preview.Open.Start` / `Snap.Preview.FirstFrame` / `Snap.Preview.TimedOut` / `Snap.Preview.Failed` | Background preview job |
| `Snap.Stages.Ready` | After post-normalize pipeline row refresh |
| `Snap.LoadGeneration.Discarded` | Stale `(ProjectId, LoadGeneration)` callback dropped |

Draft targets (fill after a measured local import on reference hardware):

| Milestone | Target |
|---|---|
| Shell bound after spine | &lt; 500 ms from `Import.Start` |
| Preview first frame | &lt; 2 s from `Shell.Bound` |
| Normalize ready | background; must not block shell bind |

Example grep:

```powershell
Select-String -Path "$env:LOCALAPPDATA\Trackdub\trackdub.log" -Pattern 'Snap\.'
```

## Follow-up measurement pass (out of scope for PR4)

1. Fill reference machine table and commit measured startup / memory / export rows.
2. Run `Trackdub.Benchmarks` on reference hardware; attach output path or summary table here.
3. Add optional CI soft thresholds (warn-only) after two baseline runs agree.
4. Long-media UI frame timing with headless or controlled `dotnet-counters` session.
5. Project-scale SQLite EXPLAIN with 10k+ segment fixtures (beyond current ~1.2k CI audit).
6. Hot-path SQLite wall-clock micro-benchmarks on the same standardized fixtures (warn-only CI after two baseline runs agree).

## Commands (local)

```powershell
# SQLite EXPLAIN audit
dotnet test tests/Trackdub.Infrastructure.Tests --filter "FullyQualifiedName~Explain" -m:1

# UI component evidence (Windows TFM)
$env:CAPTURE_UI_SCREENSHOTS = "1"
dotnet test tests/Trackdub.UI.Tests -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~ComponentScreenshot" -m:1

# Full solution build
dotnet build Trackdub.sln -m:1
```

# TensorRT RTX EP ABI plugin

TensorRT RTX is a runtime provider plugin, not a model and not a Windows ML catalog EP.

Trackdub registers the standalone ONNX Runtime EP ABI plugin before creating TRT RTX sessions, then selects the GPU `OrtEpDevice` whose EP name is:

```text
NvTensorRTRTXExecutionProvider
```

Do not accept `NvTensorRtRtxExecutionProvider` as the primary plugin identity. That spelling belonged to the deprecated Windows ML catalog wiring and must not be used for the standalone plugin route.

## Bundle layout

The plugin directory must contain these files together.

Windows:

```text
onnxruntime_providers_nv_tensorrt_rtx.dll
tensorrt_rtx_1_5.dll
tensorrt_onnxparser_rtx_1_5.dll
```

Linux (v0.3.0 cu12 linux-x64 tarball):

```text
libonnxruntime_providers_nv_tensorrt_rtx.so
libtensorrt_rtx.so
libtensorrt_onnxparser_rtx.so
libtensorrt_plugins.so
```

Companion libraries such as `tensorrt_plugins.dll` / `libtensorrt_plugins.so` are copied during install but are not part of the required readiness triple.

## Locator order

`TensorRtRtxPluginLocator` resolves the bundle directory in this order:

1. `StudioSettings.TensorRtRtxPluginDirectory`
2. `TRACKDUB_TRT_RTX_EP_DIR`
3. Default installed bundle under the Trackdub user data root (see below)

If the bundle is missing, use **Install** in Model Manager (downloads v0.3.0 cu12, persists the studio path, then registers), or run the dev fetch script.

## Fetch script (dev/CI)

From repo root on Windows or Linux x64:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
```

Optional `-InstallRoot` or `TRACKDUB_DATA_ROOT` overrides the user data root. The script verifies SHA-256 + size from `runtime/trt-rtx-ep.manifest.json`, extracts native libraries into the flat plugin directory, and prints `TRACKDUB_TRT_RTX_EP_DIR` for the current shell.

## Bundle channel (in-app + manifest)

Trackdub ships the EP ABI plugin through a pinned manifest, not the Windows ML catalog:

- **Manifest:** `runtime/trt-rtx-ep.manifest.json` (version `0.3.0`, CUDA `cu12`, per-RID archive URL + SHA-256 + size).
- **Composition copy:** `trt-rtx-ep.manifest.json` next to the app assembly (`Trackdub.Composition` `CopyToOutputDirectory`).
- **Downloader:** `TrtRtxEpBundleDownloader` verifies checksum/size, extracts required plugin files into `%UserDataRoot%/Providers/trt-rtx/0.3.0/cu12/<rid>/`.
- **In-app install:** Model Manager **Install** calls `ITrtRtxEpInstaller` after `NvidiaTensorRtRtx` license acceptance, persists `StudioSettings.TensorRtRtxPluginDirectory`, then registers via `ITensorRtRtxProviderBootstrap`.
- **Inference bootstrap:** registers an already-installed bundle only; it **never** downloads the EP bundle (same policy as WinML catalog EPs during session bootstrap).

## Download policy (install vs session)

| Path | May download TRT RTX EP bundle? |
|------|--------------------------------|
| Model Manager **Install** / bulk catalog install | Yes (after NVIDIA license acceptance) |
| CLI `trackdub providers trt-rtx install --accept-license` | Yes |
| Inference session bootstrap (`OnnxExecutionSessionFactory`) | **No** |
| Readiness / doctor / `providers trt-rtx status` | **No** |
| Benchmark harness (`BenchmarkTensorRtRtxBootstrap` with `allowProviderDownloads: true`) | Yes, only when license already accepted and bootstrap opts in |

Portable and per-user installs use the in-app or CLI install paths. A future MSI/EXE/MSIX wizard should call the same `ITrtRtxEpInstaller` after license acceptance (see [packaging/installer/README.md](../../packaging/installer/README.md)).

## Registration flow

`TensorRtRtxPluginService` owns plugin registration:

1. Verify NVIDIA hardware eligibility (Windows registry probe or Linux `/proc/driver/nvidia/gpus`).
2. Resolve and validate the plugin bundle directory.
3. Call `OrtEnv.Instance().RegisterExecutionProviderLibrary(...)` for `onnxruntime_providers_nv_tensorrt_rtx.dll`.
4. Enumerate `OrtEnv.Instance().GetEpDevices()`.
5. Require a GPU device named `NvTensorRTRTXExecutionProvider`.
6. Session creation appends that device through `SessionOptions.AppendExecutionProvider(env, devices, options)`.

TRT RTX must not call Windows ML `ExecutionProvider.TryRegister`, `EnsureAndRegisterCertifiedAsync`, or `SessionOptions.SetEpSelectionPolicy`.

## Runtime cache

Provider binaries and runtime cache are separate.

Provider bundle (installed by Model Manager or `Fetch-TrtRtxEp.ps1`):

```text
%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64\   # Windows
~/.local/share/Trackdub/Providers/trt-rtx/0.3.0/cu12/linux-x64/   # Linux
```

Manifest: `runtime/trt-rtx-ep.manifest.json` (pinned NVIDIA GitHub release URLs + checksums).

Compiled TRT RTX runtime cache:

```text
%LOCALAPPDATA%\Trackdub\EngineCache\
```

Clear the engine cache after a GPU driver change, GPU swap, or TRT RTX EP version bump when inference fails with stale compiled engines:

```powershell
trackdub cache clear engines
trackdub doctor   # shows cache path and approximate size
```

Override cache roots:

```powershell
$env:TRACKDUB_ENGINE_CACHE_ROOT = "D:\TrackdubEngineCache"
$env:TRACKDUB_CACHE_ROOT = "D:\TrackdubCache"
```

`TRACKDUB_ENGINE_CACHE_ROOT` wins. Otherwise `TRACKDUB_CACHE_ROOT\EngineCache` is used. Otherwise Trackdub falls back to `%LOCALAPPDATA%\Trackdub\EngineCache`.

## Readiness states

Keep these states separate:

- Plugin directory resolved
- All required DLLs present
- Plugin registered with ORT
- `NvTensorRTRTXExecutionProvider` GPU device visible
- Model files downloaded and checksum verified
- Model/provider pair smoke-tested
- Pipeline stage ran and produced usable artifacts

Provider registration alone is not model readiness. A failed TRT RTX plugin route may fall back to DirectML, but the selected provider must be reported as DirectML, not TRT RTX.

## Smoke commands

Readiness/probe slices:

```powershell
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~TensorRtRtxPluginLocator|FullyQualifiedName~OnnxExecutionSessionFactory" --no-restore -m:1
```

Benchmark smoke on a Windows NVIDIA RTX machine with the plugin bundle available:

```powershell
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64"
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider trt-rtx --runs 1 --format console
```

For explicit session testing, inspect the selected provider in benchmark output. Do not infer success from plugin registration logs alone.

## Benchmark / DubBench bootstrap

`Trackdub.Benchmarks` and DubBench share `BenchmarkOnnxExecutionBootstrap`:

- **WinML registry:** `ConfigureExecution(...)` before ONNX runs (same as `Trackdub.Benchmarks Program.cs`).
- **TRT RTX runner factory:** `CreateOnnxRunner()` wires `BenchmarkTensorRtRtxBootstrap` (plugin directory providers + optional license-gated bundle ensure).

Readiness paths (pick one):

1. **Model Manager** — Install after `NvidiaTensorRtRtxLicenseAccepted` in studio settings.
2. **Dev/CI fetch** — `tools/dev/Fetch-TrtRtxEp.ps1` (sets `TRACKDUB_TRT_RTX_EP_DIR`).
3. **Auto-download (opt-in)** — when `NvidiaTensorRtRtxLicenseAccepted` is true in `%LOCALAPPDATA%\Trackdub\settings.json` and benchmark/bootstrap allows provider downloads.

Headless operators:

```powershell
trackdub providers trt-rtx status
trackdub providers trt-rtx install --accept-license
trackdub doctor   # includes tensorrt-rtx-plugin probe row (no download)
```

DubBench ONNX runs call the same bootstrap before each benchmark invocation.

## Bumping TRT RTX EP release

When NVIDIA ships a new `TensorRT-RTX-EP-ABI` GitHub release:

1. Run `tools/dev/Update-TrtRtxEpManifest.ps1 -Version <x.y.z>` to refresh `runtime/trt-rtx-ep.manifest.json` (URLs, SHA-256, size).
2. Update `TensorRtRtxProviderConstants.BundledVersion`, install hints, and default install path segments if the version changed.
3. Run `tools/dev/Fetch-TrtRtxEp.ps1` locally and verify `trackdub providers trt-rtx status`.
4. Run optional GPU smoke (`.github/workflows/trt-rtx-smoke.yml`) or `Trackdub.Benchmarks --provider trt-rtx`.
5. Run `trackdub doctor` and advise users with stale engines to `trackdub cache clear engines` after upgrading the EP bundle.

## CI optional GPU tier

Default CI (`ci.yml`) stays unit/fake-backed. Optional smoke workflow: `.github/workflows/trt-rtx-smoke.yml`.

| Gate | Meaning |
|------|---------|
| Repository variable `TRACKDUB_TRT_RTX_SMOKE=true` | Enables the workflow job on self-hosted Windows with NVIDIA RTX |
| `TRACKDUB_TRT_RTX_EP_DIR` | Plugin directory after fetch (workflow sets from default install root) |
| `TRACKDUB_TRT_RTX_SMOKE=1` | Test attribute gate for optional integration tests (`RequiresTrtRtxFactAttribute`) |

The smoke job runs `Fetch-TrtRtxEp.ps1`, exports `TRACKDUB_TRT_RTX_EP_DIR`, then one `Trackdub.Benchmarks --provider trt-rtx` invocation. It uses `continue-on-error: true` until the GPU runner is stable.

## References

- [ONNX Runtime TensorRT RTX EP](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)
- [ONNX Runtime plugin EP usage](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)
- [ADR-0002 Windows ML provider strategy](../adr/ADR-0002-windows-ml-provider-strategy.md)

# Windows ML Phase 3: device policies

Optional advanced studio setting for catalog GPU sessions on Windows.

## Setting

- **Key:** `StudioSettings.WindowsMlExecutionDevicePolicy` (JSON enum name)
- **Default:** `Explicit` (unchanged production behavior)
- **UI:** Settings → Hardware → *Windows ML device policy (advanced)* (Windows only)
- **Restart:** Policy changes take effect on new sessions after save; Phase 4 evicts idle pooled sessions and invalidates the policy provider cache (restart still recommended if a leased session holds old options).

## Values

| Setting | ORT `ExecutionProviderDevicePolicy` | Behavior |
|---------|-------------------------------------|----------|
| `Explicit` | *(none)* | `GetEpDevices()` + `AppendExecutionProvider` (Phase 1–2 path) |
| `MaxPerformance` | `MAX_PERFORMANCE` | `SetEpSelectionPolicy` only; no per-EP append |
| `PreferNpu` | `PREFER_NPU` | Same |
| `MaxEfficiency` | `MAX_EFFICIENCY` | Same |
| `MinOverallPower` | `MIN_OVERALL_POWER` | Same |
| `DefaultRender` | `DEFAULT_RENDER` | Same, gated by capability probe (see below) |
| `MinPower` | `MIN_POWER` | Same, gated by capability probe (see below) |

Mapping: `WindowsMlExecutionDevicePolicyMapper` in `Trackdub.Inference.Onnx` (`#if WINDOWS`).

**Capability probe:** `DefaultRender` / `MinPower` resolve `DEFAULT_RENDER` / `MIN_POWER` by name via `Enum.TryParse` against the managed `Microsoft.ML.OnnxRuntime.ExecutionProviderDevicePolicy` surface, probed once per process. The pinned managed package version does not reliably predict when these members appear, so the probe requires **both** names to be present; if either is missing, `ApplyIfNeeded` returns early without calling `SetEpSelectionPolicy` (same as `Explicit`) instead of throwing.

## Rules

1. **Mutual exclusion:** Per session, either policy mode **or** explicit append — never both.
2. **Catalog only:** Policy applies to Windows ML catalog routes (`DirectMl`, `MIGraphX`). It does **not** select TensorRT RTX — TRT RTX uses the standalone EP ABI plugin (`trackdub providers trt-rtx`, Model Manager Install, or `Fetch-TrtRtxEp.ps1`).
3. **CPU / Kokoro:** CPU sessions never set policy; Kokoro CPU-only override unchanged.
4. **Planner / smoke:** Unchanged — `RuntimePlanFactory` and `OnnxExecutionProviderSmokeTester` still gate readiness.
5. **Fingerprint:** `BuildSessionOptionsFingerprint` includes policy key so pooled sessions do not mix explicit vs policy options.

## Seams

- Contracts: `WindowsMlEpDevicePolicyContracts.cs`, `IWindowsMlEpDevicePolicyProvider`
- Settings: `JsonStudioSettingsService.Normalize` coerces unknown enum to `Explicit`
- Composition: `StudioSettingsWindowsMlEpDevicePolicyProvider` → `OnnxExecutionSessionFactory.Initialize(bootstrapper, policyProvider)`
- Session factory: `OnnxExecutionSessionFactory.CreateSessionOptions(provider, devicePolicy, …)`

## Manual validation

After explicit matrix baseline on hardware:

1. `Explicit` — no regression vs Phase 2.
2. `MaxPerformance` — one VAD or ASR run on a **catalog** EP (`dml` / `migraphx`); log actual EP from benchmark or session metadata. Do not use this step to validate TRT RTX (see TRT RTX plugin smoke table in the stage matrix).
3. `PreferNpu` / `MaxEfficiency` — on Copilot+ PC if available; else N/A in matrix.
4. Change policy → restart → confirm new fingerprint / sessions.

**Benchmark harness:** `Trackdub.Benchmarks --windows-ml-device-policy <name>` configures `OnnxModelBenchmarkRunner`. For Windows ML catalog/device-policy routes (`dml`, `migraphx`, `auto`), non-`Explicit` policies use `SetEpSelectionPolicy` only (no explicit catalog-device append). `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT benchmark routes also never apply device policy.

## References

- [Select execution providers (device policies)](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers)
- [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md)

# Windows ML Phase 4 closeout

Production closeout for Windows ML device policies (Phase 3) and ONNX runtime alignment on Windows.

**Status:** Implementation complete in repo; hardware matrix rows remain manual on Tony PC where noted.

## Workstream A0 — Phase 3 review fixes (pre-merge)

| ID | Item | Status |
|----|------|--------|
| A0.1 | Cache `StudioSettingsWindowsMlEpDevicePolicyProvider` after first load | Done |
| A0.2 | Legacy pool fingerprint for `Explicit` / CPU (no policy key); catalog GPU non-explicit includes policy | Done |
| A0.3 | `TolerantWindowsMlExecutionDevicePolicyJsonConverter` | Done |
| A0.4 | Expanded `ShouldUseCatalogDevicePolicy` tests | Done |
| A0.5 | `ShowsWindowsMlDevicePolicyPanel` on `IDesktopPlatformService` | Done |
| A0.6 | Corrupt settings: log, timestamped `.corrupt` backup, defaults | Done |
| A0.7 | Thread-safe `OnnxExecutionSessionFactory.Initialize` (first call wins) | Done |

## Workstream A — Land Phase 3

- Build: `dotnet build Trackdub.sln`
- Tests: `dotnet test tests/Trackdub.Infrastructure.Tests tests/Trackdub.Inference.Tests`
- Windows TFM (local): `dotnet build Trackdub.sln -f net10.0-windows10.0.19041.0`

## Workstream F — ORT native alignment (P0)

**Problem:** Managed ORT 1.24.x vs WinML-bundled native 1.17.x caused API version 24 errors.

**Mitigation:** Resolver prefers managed-package `runtimes/win-*/native` before app base; benchmarks log managed ORT assembly version.

**Verify:**

```powershell
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <path> --provider trt-rtx --runs 1 --format console
```

## Workstream C — Pool eviction + policy cache

`InferenceSessionPool.EvictAllIdleAsync`, `IInferenceSessionPoolEvictor`, settings-save eviction + cache invalidation.

## Workstream D — Benchmark CLI

`--windows-ml-device-policy explicit|max-performance|prefer-npu|max-efficiency|min-overall-power`

(Additional policies have landed since this Phase 4 closeout; see [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md) for the current, canonical value list.)

Windows ML catalog/device-policy benchmark routes (`dml`, `migraphx`, `auto`) follow the same mutual-exclusion rule as the studio session factory: non-`Explicit` policies call `SetEpSelectionPolicy` only; explicit append runs only when policy is `Explicit`. `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT routes ignore device policy.

## Workstream B — Hardware matrix (manual)

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) after F passes on hardware.

## Workstream E — ADR

[ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md) Phase 4 section.

## Related

- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)

# Windows ML Phase 5: catalog EP expansion (OpenVINO, QNN)

Internal checklist for Intel OpenVINO, Qualcomm QNN, AMD MIGraphX, and AMD VitisAI **Windows ML catalog** routes. TensorRT RTX is not a Windows ML catalog route anymore; it uses the standalone ORT EP ABI plugin documented in [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md).

## Relationship to existing paths

| Path | Enum | When used |
|------|------|-----------|
| Standalone OpenVINO | `ExecutionProviderKind.OpenVino` | Linux; optional Windows install via component downloader (`Infrastructure`). |
| WinML catalog OpenVINO (stub) | `ExecutionProviderKind.OpenVinoCatalog` | Future Windows catalog append; **not** the same as standalone OpenVINO. |
| WinML catalog QNN (stub) | `ExecutionProviderKind.Qnn` | Future Snapdragon / NPU catalog EP on Windows. |

Do not duplicate “GPU ready” semantics between standalone OpenVINO install state and catalog registration.

## Code seams

| Concern | Location |
|---------|----------|
| Catalog provider name constants | `Trackdub.Inference.Onnx/WindowsMl/WindowsMlCatalogProviderIds.cs` |
| Registration / bootstrap | `WindowsMlProviderRegistrationPolicy.cs`, `WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Discovery | `OnnxExecutionProviderDiscovery.cs` — stub kinds return **unavailable** |
| Session append | `OnnxExecutionSessionFactory.cs` — `NotSupportedException` until smoke path exists |
| Milestone probe order | `StageRuntimeRequirements.cs` — **unchanged in 5c** |

Stub marker: `#TODO(phase-5-catalog-ep)` in code; reference this doc and [ADR-0002 Phase 5](../adr/ADR-0002-windows-ml-provider-strategy.md).

## Suggested smoke commands (when hardware exists)

Windows TFM:

```powershell
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --help
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider dml
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <model-id> --windows-ml-device-policy PreferNpu
```

When QNN / catalog OpenVINO CLI aliases exist, add matrix rows here. For TRT RTX smoke commands, use the plugin doc instead of this catalog checklist.

## Matrix rows to add (when hardware exists)

| Hardware | Stage | Catalog EP to exercise | Notes |
|----------|-------|------------------------|-------|
| Intel GPU box | VAD / ASR | OpenVINO catalog | Distinct from standalone OpenVINO row |
| Snapdragon / Copilot+ PC | VAD / ASR | QNN | Pair with `PreferNpu` policy smoke; mark N/A if no NPU |

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) with pass/fail and **actual EP** from console — never infer from policy name alone.

## Enablement order (post-5c)

1. Confirm catalog ids in `WindowsMlCatalogProviderIds`.
2. Implement session append + smoke tester path.
3. Run stage matrix smoke on target hardware.
4. Update manifest `expected_runtime` and stage allow-lists if product commits.
5. Only then extend `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`.

## References

- [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md)
- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)
- [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md)
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md)

# Windows ML stage provider matrix (Phase 2)

Internal audit companion for [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md) stage catalog alignment.

## Planner intersection

`RuntimePlanFactory.GetOrderedProviders` orders providers as:

`Milestone5PlanningPolicy.SupportedProvidersThisMilestone` ∩ `AllowedProvidersThisMilestone` (or engine-family override).

Phase 2+ sets default stage allow-lists to `StageRuntimeRequirementsCatalog.DefaultOnnxStageAllowedProviders` (same sequence as milestone probe order) for ONNX spine stages. **`kokoro` engine-family override remains CPU-only** (ConvTranspose mechanical block).

Milestone probe order (2026-06): `TensorRTRtx` → `Migraphx` → `OpenVinoCatalog` → `Qnn` → `VitisAi` → `TensorRt` → `Cuda` → `OpenVino` → `DirectMl` → `Cpu`. Catalog EPs are in the intersection list; discovery + smoke-test gating still determine whether a route becomes `Verified`.

## Stage defaults (current)

|Stage|Default allow-list|Engine-family overrides|
|-|-|-|
|VAD|Milestone default|—|
|ASR|Milestone default|`whisper-genai` keeps GenAI-oriented list|
|Translation|Milestone default|`madlad`, `phi-genai`|
|Diarization|Milestone default|—|
|Separation|Milestone default|`spleeter`|
|OverlapRescue|Milestone default|`sepformer`|
|SpeechEnhancement|Milestone default|`deepfilternet3`|
|LipSync|Milestone default|`onnx-ctc-phoneme-aligner`|
|LipSynthesis|Milestone default|`latentsync-diffusion`|
|TextRefinement|Milestone default|—|
|TTS|Milestone default|**`kokoro` → CPU only** (ConvTranspose / DirectML incompatible)|

## Windows manual smoke checklist

Run on a machine with the relevant catalog EP installed before claiming GPU readiness in release notes. Record pass/fail in the PR or issue; planner must fall through on smoke failure (no fake readiness).

|Stage|Representative model|Catalog EP to exercise|Kokoro / CPU guard|
|-|-|-|-|
|VAD|`silero-vad`|TensorRT RTX (NVIDIA) or MIGraphX (AMD)|—|
|Diarization|Sortformer 4spk v2.1|Same|—|
|Translation|Any bundled `opus-*` ONNX pair|Same|—|
|TTS|`chatterbox-*`|Same|**Kokoro plan must stay CPU**|
|ASR|`whisper-tiny-onnx`|CUDA / DirectML / catalog per discovery|GenAI path separate|

### Windows smoke (Tony PC, RTX 5080, 2026-05-23)

|Stage|Result|Actual EP|
|-|-|-|
|VAD|pass|tensorrt-rtx (prior smoke); benchmark `dml` pass 2026-05-23|
|Diarization|pass|tensorrt-rtx|
|Translation|N/A|opus ONNX pair not present under `models/` on benchmark host (manifest aliases only)|
|TTS chatterbox|pass|tensorrt-rtx|
|Kokoro CPU guard|pass|cpu|
|ASR|pass|directml|

Suggested commands:

```powershell
dotnet build Trackdub.sln
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~RuntimePlanner"
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --help
```

## Manifest `expected_runtime` (Phase 2)

Bundled ONNX models use canonical token:

`windows-ml|onnxruntime-migraphx|onnxruntime-directml`

Legacy token `onnxruntime-directml|onnxruntime-migraphx` remains parseable in `ModelExpectedRuntimeFormatter` for older manifests.

This field is **governance / Model Manager hints only**; the runtime planner does not read `expected_runtime`.

## Device policy mode smoke (Phase 3)

Set **Settings → Windows ML device policy** to each non-default value, **restart Trackdub**, then run one stage per policy. Harness shortcut: `Trackdub.Benchmarks --model silero-vad --provider dml --windows-ml-device-policy <name>` (catalog GPU; policy mode uses `SetEpSelectionPolicy`).

| Policy | Stage exercised | Pass/fail | Actual EP | Notes |
|--------|-----------------|-----------|-----------|-------|
| Explicit | silero-vad (benchmark) | pass | dml | Explicit append path |
| MaxPerformance | silero-vad (benchmark) | pending | dml/migraphx catalog route only | TRT RTX is no longer selected by Windows ML device policy; use the TRT RTX plugin smoke commands in `tensorrt-rtx-ep-abi-plugin.md`. |
| PreferNpu | silero-vad (benchmark) | pass | dml | No NPU on host; ORT fell back to DML |
| MaxEfficiency | silero-vad (benchmark) | pass | dml | |
| MinOverallPower | silero-vad (benchmark) | pass | dml | |

See [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md).

## TensorRT RTX EP ABI plugin smoke (separate from Windows ML policy)

TRT RTX is **not** a Windows ML catalog EP and is **not** selected by `WindowsMlExecutionDevicePolicy` (including `MaxPerformance`). Validate it with the standalone plugin route only.

| Stage | Representative model | Command / surface | Pass/fail | Actual EP | Notes |
|-------|---------------------|-------------------|-----------|-----------|-------|
| VAD | `onnx-community/silero-vad` | `Trackdub.Benchmarks --provider trt-rtx` | pending | *pending local GPU run* | Requires NVIDIA GPU + plugin bundle |
| Headless status | — | `trackdub providers trt-rtx status` | pending | JSON `isOrtProviderListed` | Probe-only; no download |
| Headless install | — | `trackdub providers trt-rtx install --accept-license` | pending | — | License-gated bundle download |
| DubBench | same as benchmark | DubBench ONNX run after shared bootstrap | pending | — | Uses `BenchmarkOnnxExecutionBootstrap` |

Prerequisites: [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md) (Model Manager, `Fetch-TrtRtxEp.ps1`, or license-accepted auto-download). Optional CI: `.github/workflows/trt-rtx-smoke.yml` when repository variable `TRACKDUB_TRT_RTX_SMOKE=true`.

Suggested smoke:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64"
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model onnx-community/silero-vad --provider trt-rtx --runs 1 --format console
trackdub providers trt-rtx status
```

## Phase 4 verification (2026-05-23)

Automated closeout: ORT native resolver prefers managed-package `runtimes/win-*/native` before app base; benchmark CLI `--windows-ml-device-policy`; pool eviction on settings save. See [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md).

## Phase 5 catalog EP stubs (2026-05-23)

OpenVINO catalog + QNN + VitisAI documented in ADR-0002 Phase 5 and [windows-ml-phase-5-catalog-eps.md](windows-ml-phase-5-catalog-eps.md). They are included in `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`; discovery may still report unavailable until installed, and smoke failure falls through to the next provider.
