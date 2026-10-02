# Terminology

Vocabulary as this repo actually uses it. When a term is absent from this file, check `AGENTS.md`, then the source, before inventing one.

## Pipeline stage names

Canonical constants live in `src/Trackdub.Domain/StageRuns/StageNames.cs`. **Every `StageRunRecord.Start` call site must use a `StageNames.*` constant, never an inline literal** — `StageNameConsistencyTests` fails the build on any known stage name passed as a string. Adding a stage name means editing both `StageNames.cs` and `KnownStageNameValues` in the test.

| Constant | Value | Role |
|---|---|---|
| `AudioPreparation` | `audio-preparation` | normalize/degrade input before speech work |
| `Vad` | `vad` | voice activity detection; speech regions |
| `Diarization` | `diarization` | who spoke when |
| `Asr` | `asr` | transcript from audio |
| `OverlapRescue` | `overlap-rescue` | recover overlapping speech lost to VAD |
| `TextRefinementAsr` | `text-refinement-asr` | re-decode text-refinement audio |
| `Translation` | `translation` | translated lines |
| `TextRefinement` | `text-refinement` | polish translated text |
| `TextRefinementTranslation` | `text-refinement-translation` | re-translate refined text |
| `Separation` | `separation` | stem separation; feeds voice-clone reference clips and the mix bed |
| `Tts` | `tts` | synthesized dubbed audio per segment |
| `LipSync` | `lip-sync` | runs after TTS, needs takes to exist |
| `Export` | `export` | final render |
| `LipSynthesis` | `lip-synthesis` | runs after Export, needs ExportAudio |
| `PreviewMix` | `preview-mix` | preview render, not the shipping export |
| `SpeechEnhancement` | `speech-enhancement` | denoise/enhance; degrades in place to FFmpeg/AFX |
| `SpeakerAssignment` | `speaker-assignment` | speaker-to-voice binding |

Also defined but not in the runner orders: `StageNames.SpeechEnhancement`, `StageNames.PreviewMix`, `StageNames.SpeakerAssignment`, `StageNames.TextRefinementTranslation`.

### Stage order and gating

`src/Trackdub.Application/Dubbing/DubbingPipelineStages.cs` is the canonical metadata for headless/CLI callers:

- `DefaultStageOrder` (full dubbing run, lip stages omitted — they are opt-in via StageFilter): `vad → diarization → asr → translation → separation → tts → export`
- `ExtendedStageOrder` (full catalog used to resolve `StageFilter`): `audio-preparation → vad → diarization → asr → overlap-rescue → text-refinement-asr → translation → separation → tts → lip-sync → export → lip-synthesis`
- `PrerequisiteStages` (failure blocks everything downstream): `vad`, `asr`, `translation`, `tts`. **Separation is not one** — without stems, TTS reference clips come from the mix and export uses the original bed.
- `RequiresSourceMedia(stage)` and `RequiresTargetLanguage(stage)` gates.
- `RuntimeModelSetupWorkflow.IsOptionalRuntimeStage`: `Separation` and `SpeechEnhancement` only. Separation has no fallback; SpeechEnhancement degrades in place, so declining its model must not abort the run.

Ordering rationale worth remembering: separation runs *after* transcription because its stems feed reference clips and the mix bed, not the transcript stages, which route from the full mix.

## Dubbing domain vocabulary

```
local media → audio preparation → speech detection (VAD) → speaker analysis (diarization)
           → transcript (ASR) → translation → voice / TTS → preview → mix → export
```

| Term | Meaning in this repo |
|---|---|
| ingest | `audio-preparation`: probe, extract, normalize source media into project artifacts |
| transcript | segment/turn/word structure produced by ASR, revisioned |
| translate | target-language lines derived from transcript segments |
| voice | per-speaker voice assignment; cloning is consent-gated and license-aware |
| TTS | per-segment synthesized audio; "takes" are candidate generations |
| timing | stage timing/telemetry, distinct from **timed output** (export with target-language duration match) |
| preview / export | `preview-mix` is a review render; `export` is the shipping artifact |
| speaker | diarization cluster promoted to `ProjectSpeaker` with turns |

Two "timing" senses collide constantly. Say which one: **stage timing** (how long a stage took, telemetry) vs **duration match** (export audio duration against target-language speech duration).

## Snapshot

`ExecutionSnapshot` (`IReadOnlyDictionary<string, string>` on `DubbingRunResult`, carried through `TranscriptGenerationContext`) is the per-run fingerprint of model/provider/variant choices. A prior run's artifacts are only reusable when the current snapshot matches. It is an **identity value, not a verdict** — `StageRuntimePlan.ModelRevisionHash` likewise is an identity, while `ModelIntegrityStatus` is the integrity truth (it may be `Skipped`). Evaluating one selection set and running another is the classic defect: provision and execute must read the same snapshot.

`src/Trackdub.Domain/Media/MediaProbeSnapshot.cs` and `src/Trackdub.Domain/Benchmarking/ResourceUsageSnapshot.cs` are unrelated record types with the same word in the name.

## Artifact routing

- **Artifact** — a produced file, represented in Domain by `ProjectArtifact` (Id, ProjectId, MediaAssetId, Kind, RelativePath, Sha256, SizeBytes, DurationSeconds, SampleRate, ChannelCount, CreatedAtUtc, StageRunId?, Provenance?, DegradationCode?, DegradationStage?). Domain stores metadata only, never bytes.
- **Write handle** — `IArtifactStore.CreateWriteHandle(relativePath)` returns an `ArtifactWriteHandle` (relativePath, finalPath, temporaryPath); `CommitAsync` promotes it. Write-then-commit is how artifacts avoid half-written resume state.
- **Layout** — project folder `*.trackdub/` with `trackdub.db`, `manifest.json`, `media/`, `artifacts/`, `logs/`, `temp/`. The canonical directory list is `ProjectArtifactPaths.RequiredDirectories` in `src/Trackdub.Contracts/Projects/ProjectArtifactPaths.cs` — read it rather than assuming; it contains `artifacts/{audio,degradation,mix,preview,export,reference-clips,stems,translation,transcript,tts,waveform,overlap-rescue}` and `artifacts/audio/{quality,speech-enhancement,speech-processing}`, and it changes.
  Two outputs do **not** have their own directory: speech regions are written to `artifacts/audio/speech-regions-<runId>.json` (`GetSpeechRegionsRelativePath`) and diarization results to `pipeline/diarization-result-<runId>.json` (`GetDiarizationResultRelativePath`). There is no `artifacts/vad` and no `artifacts/diarization` — do not go looking for them.
  Machine-local state is separate: `%LocalAppData%/Trackdub/{settings.json,models/,model-cache/,benchmarks/,logs/}`.
- **Resume** — valid existing artifacts make a stage `Satisfied` (skip with `EXISTING_ARTIFACTS_VALID`); `TranscriptPipelineResumeHydrator` rehydrates context from them. On skip or failure, **preserve the original artifacts** and record the reason code.
- **Audio routing** — `TranscriptAudioRoutingPlan` decides which artifact feeds VAD, ASR, and diarization. ASR routes to the *unprocessed* full mix: speech enhancement and separation measurably degraded ASR, while VAD and diarization keep the enhanced audio.
- **Degradation** — `PipelineDegradationRecord` marks an artifact as degraded and by which stage.

## Provider selection

`ExecutionProviderKind` → `StageRuntimeRequirements` (allowed providers per stage) → `RuntimePlanner` → `StageRuntimePlan` carrying `ExecutionProvider`, `ModelId`, `ModelAlias`, `EngineFamily`, `ModelTier`, `Variant`, `DeviceIndex`, `ModelIntegrityStatus`, `RequirePreferredExecutionProvider`. `RequirePreferredExecutionProvider` is a hard pin: session factories must **not** soft-retry a preferred EP init onto DirectML/CPU. When true, say which EP was actually used; never silently swap.

Aliases are soft ranking hints inside `StageRuntimeRequirements`, resolved by `ModelManifestAliasResolver` against manifest `aliases[]`.

## Project and session

- **Project** — `TrackdubProject` (Domain) plus its `*.trackdub/` folder and `trackdub.db`. Owns media assets, speakers, transcripts, artifacts.
- **Session** — a run-scoped handle (`IDubbingSession`, `TranscriptWorkspace`, `TranscriptWorkspaceSession` from Composition) exposing services, options, and the execution snapshot. Sessions commit stage runs and drive downstream invalidation; view-model state does not live here.
- **Workspace context** — `ITranscriptWorkspaceContext` / `TranscriptWorkspaceContext` in Contracts/Composition; the composition seam that hands a session its services.
- **Stage run** — `StageRunRecord` persisted per stage: kind, input/output artifact IDs, model/provider/runtime metadata, settings hash, started/completed, status, warnings, error classification. Stale `Running` rows are reconciled after a crash (`StageRunHygiene`).
- **Skip reason codes** — `src/Trackdub.Domain/StageRuns/StageSkipReasonCodes.cs`: `EXISTING_ARTIFACTS_VALID`, `PREREQUISITE_FAILED`, `NO_TRANSCRIPT_SEGMENTS`, `NO_SPEECH_REGIONS`, `DISABLED_BY_OPTION`, `OPTIONAL_MODEL_DECLINED`. All six are "benign" via `IsBenignSkip` — benign means *intentional gating*, not success.
- **Transient fault** — `TransientFailureKind` (user cancellation, directory lock, sqlite busy, ffmpeg exit, model download, starter-pack, memory exhausted, device timeout, unknown), surfaced via `PipelineTransientFault` on `PipelineTransientFaultBus`. Distinct from permanent failure.

## The distinction pairs this repo cares about

| Pair | Why they are not the same |
|---|---|
| **disabled vs ran** | `DISABLED_BY_OPTION` means the stage never executed. No timing, no evidence. |
| **skipped vs succeeded** | `EXISTING_ARTIFACTS_VALID` is a resume. It is not a success of the current run and not a speed sample. `PREREQUISITE_FAILED` is a skip, not a pass. |
| **registered vs installed** | DI registration says nothing about whether the runtime/EP exists on this machine. |
| **repo license vs weights commercial** | The Apache-2.0 core repo says nothing about model weights. Commercial use requires verified manifest evidence; unknown is unsafe. |
| **duration match vs lip-sync quality** | An export that hits target duration can still fail visual lip-sync. Separate claims, separate evidence. |
| **Ready vs Verified (plan status)** | `Ready` = files present and integrity ok. `Verified` = a smoke test actually executed the EP. CPU never smoke-tests, so CPU stops at `Ready`. |
| **planned vs executed** | `StageRuntimePlan` is a plan. Only a `StageRunRecord` terminal state plus a usable artifact proves execution. |
| **succeeded vs useful output** | A stage can complete and emit a degraded artifact. `DegradationCode` marks it. |

## Vocabulary traps

- "Model" — manifest *entry* vs downloaded *file* vs loaded *session*. Name which.
- "Provider" — DI provider, execution provider, or cloud engine alias. Three different things.
- "Session" — `IDubbingSession`, ONNX Runtime inference session, or playback session. Name the type.
- "Stage" — `RuntimeStage` enum value (11 members, inference-relevant) vs `StageNames` string (17 constants, run/persistence-relevant). They do not map 1:1; `preview-mix`, `export`, `audio-preparation`, `speaker-assignment`, and the text-refinement variants exist only as `StageNames`.
- "Ready" — never a bare word. Say which rung of the readiness ladder.

## Related

- `context/standards/architecture-rules.md` — the never-fake-readiness rule set and boundary catalogue
- `context/domain/architecture.md` — layer map
- `context/domain/inference-stack.md` — readiness ladder detail