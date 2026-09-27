# Service Blueprint — First Dub End-to-End

**Scenario:** A first-time user opens Trackdub, imports one local video, and produces + exports a dubbed result.
**State:** As-is (current build) with To-be (Δ) deltas side by side.
**Method:** Lanes grounded in real code (handlers, services, manifest), not aspiration. Component names are clickable anchors for coordination.

> **Read this as a system, not a feature list.** The payoff is where lanes *disconnect* — a frontstage promise the backstage can't keep, a long backstage stretch with no user touchpoint, or a single support dependency that takes a whole phase down. Those are collected in the [Gap Register](#gap-register); the To-be column is the fix for each.

---

## 0. Lanes & lines (legend)

| Lane | Trackdub meaning |
|---|---|
| **Physical evidence** | Screens, dialogs, waveform/timeline, the exported MP4 + subtitles |
| **User actions** | What the user does in the shell |
| **Frontstage** *(visible)* | App responses the user sees: progress, modal dialogs, candidate auditions |
| **Backstage** *(invisible)* | FFmpeg + ONNX work, artifact writes, persistence |
| **Support** | Model manifest, models on disk, execution providers, FFmpeg binaries, SQLite, **optional cloud APIs** |

**— line of interaction —** between User actions and Frontstage
**— line of visibility —** between Frontstage and Backstage
**— line of internal interaction —** between Backstage and Support

---

## 1. One-glance swimlane matrix (as-is)

Phases collapse the 7 canonical pipeline stages into user-facing steps. Stage internals live in Backstage under **Transcribe & Translate** — they are not separate user steps (altitude discipline). *Verified against `TrackdubDubbingEngine.DefaultStageOrder` — see [Validation log](#validation-log--backstage--support-lanes-verified-against-source).*

| Lane ↓ / Phase → | 1 · Launch | 2 · Import | 3 · Configure run | 4 · Transcribe & translate | 5 · Review & assign voices | 6 · Preview mix | 7 · Export |
|---|---|---|---|---|---|---|---|
| **Physical evidence** | Empty 5-panel shell, titlebar | MediaBin thumbnail, waveform | RunConfig, PipelineStages list, source-lang picker | Per-stage progress rows; readiness/consent modals | Segment list/detail, speaker cards, TTS candidate selector, glossary | Mini preview player, timeline, transport bar | ExportMix dialog → MP4 + .srt + manifest on disk |
| **User actions** | Launch app, (open/new project) | Drag-drop / pick file | Pick source+target lang, toggle stages, pick model tier | Click **Run**, then *wait* | Edit text, rename/merge speakers, assign voice per speaker, audition candidates | Scrub, play, judge sync/mix | Choose format, click **Export** |
| **Frontstage** *(visible)* | Shell composition, last-project restore | Probe result, waveform render, duration badge | Stage enable/skip reasons, EP/model readiness badges | Stage status (running/skipped/failed), **modal setup + license + clone-consent dialogs mid-run** | Inline edits commit, candidate playback, fallback-voice dialog | Range render + playback | Progress, success toast, reveal-in-folder |
| **Backstage** *(invisible)* | DI graph build, settings load | `FfmpegMediaProbe`, `FfmpegAudioExtractionService`, waveform summary, ingest write | Stage planning, device-affinity resolve *(pre-flight is at run start, not here)* | Separation→VAD→Diarization→ASR→Translation (canonical order); ONNX sessions; atomic artifact writes + run-level resume | `StartTtsStageHandler`, `GenerateCandidatesHandler`, TTS synth, **WSOLA/ffmpeg time-stretch to fit takes to duration** | `PreviewMixWorkflow` + `PreviewRangeRenderer`: gains/ducking/room-tone/pan/downmix | `MixPlanBuilder` → full mix → `FfmpegMuxer`, `SubtitleExportService` (takes already time-fitted at TTS) |
| **Support** | SQLite, studio settings, app log | FFmpeg binaries (auto-download), temp dirs | Bundled manifest, model registry, HW profiler | **models/ on disk + HF download**, checksum verify, **EP setup (TRT-RTX plugin/DML/CUDA/WinML catalog)**, Olive optimize, license catalog, **opt. cloud ASR/MT** | kokoro voices, **opt. cloud TTS (ElevenLabs/OpenAI)**, clone-consent | FFmpeg encoders, room-tone impulse | FFmpeg mux/encoder selection, export manifest store |

---

## 2. Per-phase detail (as-is → to-be Δ)

Each phase: 5-lane table. **As-is** = what the build does today. **To-be Δ** = the change that closes the gap surfaced in that lane.

### Phase 1 · Launch
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Empty 5-panel shell; no welcome/onboarding screen (grep found none) | First-run welcome surface: what Trackdub does, privacy/local-first statement, "try sample clip" |
| User actions | Launch; optionally open/new project | Same; guided first path instead of blank canvas |
| Frontstage | Shell composition, last-project restore | Add first-run state branch + privacy/cloud disclosure |
| Backstage | DI graph build (`CompositionRoot`), settings load | Detect "no projects yet" → route to onboarding |
| Support | SQLite, `JsonStudioSettingsService`, app log | Persist `firstRunCompleted`; ship a tiny bundled sample media |

> **Gap G1 — no onboarding.** First dub starts on a blank shell; the user must already know the flow.

### Phase 2 · Import
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | MediaBin item, waveform, duration badge | Same + codec/encoder compatibility chip up front |
| User actions | Drag-drop or pick file | Same |
| Frontstage | Probe result + waveform | Surface probe warnings (unsupported codec, no audio track) **before** Run, not at stage failure |
| Backstage | `FfmpegMediaProbe`, `FfmpegAudioExtractionService`, `WaveformSummaryGenerator`, `ProjectMediaIngestService` | Validate audio presence/encoder support during ingest; persist verdict |
| Support | **FFmpeg binaries — auto-download on demand** | Pre-flight FFmpeg health (`FfmpegHealthCheck`) at import, not first FFmpeg call |

> **Gap G2 — FFmpeg is a silent single point of failure.** If `FfmpegAutoDownloader` hasn't resolved a binary, failure surfaces deep in a backstage stage, far from the import action that caused it.

### Phase 3 · Configure run
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | RunConfig, PipelineStages, stage-options, source-lang picker | Add target-language + cloud-vs-local toggle with explicit privacy note per stage |
| User actions | Pick source+target lang, toggle stages, pick tier | Same + opt into cloud per stage knowingly |
| Frontstage | Skip/enable reasons, readiness badges | Show **which engine each stage will use (local model vs named cloud provider)** before Run |
| Backstage | Stage planning, `DeviceAffinitySettings` — **model pre-flight is deferred to run start, not done here** | Run pre-flight + engine resolution **here**, before the user commits to Run |
| Support | Manifest, model registry, HW profiler | `CloudAwareTranslationEngine` / `CloudAwareTtsEngine` selection made visible, not implicit |

> **Gap G3 — engine selection is invisible.** `CloudAware*` wrappers silently choose local vs cloud at run. The user can't see, before pressing Run, that segment text/audio may egress to DeepL/OpenAI/Gemini/ElevenLabs.

### Phase 4 · Transcribe & translate  *(the long backstage stretch)*
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Per-stage progress rows; **modal** setup/license/consent dialogs appear mid-run | Inline per-stage ETA + non-blocking readiness resolved **before** Run |
| User actions | Click **Run**, then wait through 7 stages | One Run; readiness prompts front-loaded, not interrupting |
| Frontstage | Stage status + structured skip/fail reasons; `RuntimeModelSetupDecisionDialog`, `ModelNotReadyDialog`, `DiarizationModelSetupDecisionDialog`, `EpVendorLicenseDialog` | Move readiness/consent to Configure; during Run show progress + ETA + cancel only |
| Backstage | Canonical order (`DefaultStageOrder`): Separation (spleeter/mrx) → VAD (silero) → Diarization (sortformer, **before** ASR) → ASR (whisper/qwen3) → Translation (opus-mt/madlad/phi). Prep/enhance + qwen2.5 refine are sub-steps, **not** canonical stages. **VAD & ASR are blocking prerequisites** (failure → later stages `PREREQUISITE_FAILED`; others → PartialSuccess). Atomic writes + run-level resume skip stages matching the `ExecutionSnapshot` | Emit per-stage progress %/ETA; same prerequisite + resume guarantees |
| Support | models/ + HF download, **checksum verify**, EP install + Olive optimize, license catalog; **opt. cloud ASR/MT (BYO key, routed by model alias)**. `RuntimePlannerPreFlightChecker` plans model+EP per stage; **auto-downloadable VAD/ASR/Diar models are fetched mid-stage, not before Run** | Pre-stage all downloads/optimization in Configure; cloud egress logged + consented |

> **Gap G4 — one action, then a long blind wait.** Run is a single user touchpoint followed by 7 sequential backstage stages. Progress exists per stage but **no ETA**; first-run model download + Olive optimization + EP install can run for minutes inside this stretch.
> **Gap G5 — readiness is partly front-loaded, partly reactive (inconsistent).** `RuntimeModelSetupCoordinator.EnsureImportModelsAvailableAsync` runs the setup decision loop (Download / Import / Skip) at **import** — good, front-loaded. But model-tier, diarization, and voice decisions made *after* import surface their setup dialogs **mid-run**, and the headless SDK path throws instead of prompting. Only **Separation** is skippable (`IsOptionalRuntimeStage`). The gap is the inconsistency, not total absence — softened from v1 by the import-time front-loading.

### Phase 5 · Review & assign voices
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Segment list/detail, speaker cards, glossary panel, TTS candidate selector | Same + diff highlight of edited vs original segments |
| User actions | Edit text, rename/merge speakers, assign voice per speaker, audition candidates | Same |
| Frontstage | Inline edit commit, `FallbackVoiceGenerationDialog`, `VoiceCloneConsentDialog`, candidate playback | Surface confidence/low-quality segments first (`TranscriptConfidenceEvaluator`) |
| Backstage | `SegmentEditingService`, `SpeakerAssignmentService`, `StartTtsStageHandler`, `GenerateCandidatesHandler`, TTS synth + post-process. **Timing reconciliation lives here** — `TtsOrchestrationService` fits each take to its segment duration via WSOLA/ffmpeg time-stretch (`AudioTimeStretchService`, `WsolaPhonemeStretchService`) | Same; persist audition choices as durable artifacts |
| Support | kokoro voices; **opt. cloud TTS (ElevenLabs/OpenAI)**; clone-consent gate | Cloud TTS egress consented + logged like cloud MT |

> **Gap G6 — voice-clone consent exists, but cloud-TTS egress is a separate unflagged boundary.** `VoiceCloneConsentDialog` gates cloning; sending dialogue text to ElevenLabs/OpenAI for synthesis is a distinct privacy event that should be equally explicit.

### Phase 6 · Preview mix
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Mini preview player, waveform timeline, transport bar | Same + A/B original-vs-dub toggle |
| User actions | Scrub, play a range, judge timing/mix | Same |
| Frontstage | `PreviewMixWorkflow` range render + playback (libmpv) | Faster incremental preview; show mix gain per track |
| Backstage | `PreviewRangeRenderer`: source/dubbed/**ducking** gain staging + optional room-tone timbre-polish (0.3s source pre-roll convolution, `RoomToneConvolver`) + optional pan-restore (original L/R RMS) + multichannel→stereo downmix. **Range-only** render, tracked as `StageNames.PreviewMix`. *Loudness norm is NOT here — separate `FfmpegLoudnessNormalizer` at extraction* | Cache rendered ranges |
| Support | FFmpeg encoders, libmpv natives, room-tone impulse | libmpv health checked at startup (see playback-native-layout.md) |

### Phase 7 · Export
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | ExportMix dialog → MP4 + .srt + export manifest on disk | Same + export summary (engines used, cloud calls made, attribution) |
| User actions | Choose format, click Export | Same |
| Frontstage | Progress, success, reveal-in-folder | Show what shipped: which models/cloud providers + required attributions |
| Backstage | `MixPlanBuilder` → full-length mix (same DSP as preview) → `FfmpegMuxer`, `SubtitleExportService`, `ExportManifestModels`. **Timing reconciliation already happened at TTS — not here** | Emit attribution/provenance into manifest |
| Support | FFmpeg mux + encoder selection, mix-plan store | Encoder capability pre-checked (`FfmpegVideoEncoderCapabilityService`) before Export, not at mux |

> **Gap G7 — no provenance at the finish line.** Several bundled models require attribution (sortformer, spleeter, mrx, kokoro) and cloud providers have their own terms. The export doesn't tell the user what was used or what attribution they owe.

---

## Gap Register

Ranked by how badly a lane disconnect hurts the first-dub experience. Each maps to the To-be Δ above.

| # | Gap | Lanes that disconnect | Severity | Fix (to-be) |
|---|---|---|---|---|
| **G5** | Readiness **inconsistent**: front-loaded at import, but post-import tier/diar/voice changes interrupt mid-run (headless throws) | Frontstage ↔ Support, across Phase 4 | **Med** *(was High; softened by import front-loading)* | Make import-time setup the single gate; re-validate at Configure when selections change |
| **G4** | **Run = one action then a long blind wait**; per-stage progress but no ETA; first-run downloads/optimize hide here | User actions ↔ Backstage, Phase 4 | **High** | Per-stage ETA; pre-stage downloads + Olive optimize before Run |
| **G3** | **Local-vs-cloud engine choice is invisible** before Run; data may egress unknowingly | User actions ↔ Support, Phase 3 | **High** | Per-stage engine badge (local model vs named cloud provider) at Configure |
| **G2** | **FFmpeg auto-download is a silent SPOF**; failure surfaces deep in a stage | Frontstage ↔ Support, Phase 2 | **Med** | FFmpeg health-check at import, not at first FFmpeg call |
| **G1** | **No onboarding**; first dub starts on a blank shell | Physical evidence ↔ User actions, Phase 1 | **Med** | First-run welcome + privacy statement + sample clip |
| **G6** | **Cloud-TTS egress** is an unflagged boundary distinct from clone consent | Frontstage ↔ Support, Phase 5 | **Med** | Consent + log cloud TTS egress like cloud MT |
| **G7** | **No provenance/attribution** in the exported result | Backstage ↔ Physical evidence, Phase 7 | **Low** | Export summary: engines used, cloud calls, attributions owed |

### Reading the blueprint (the four classic signals)

- **Frontstage promises the backstage can't keep:** G5 — the run *looks* like a single press, but the backstage demands setup decisions it surfaces as surprise modals.
- **Single support dependency takes a phase down:** G2 (FFmpeg), and libmpv for Preview — both are auto-resolved natives with no early health gate.
- **Long horizontal stretch with no user touchpoint:** Phase 4 — 7 sequential stages behind one Run; the user is waiting and (G4) under-informed about how long.
- **Cross-cutting support fragility made invisible:** G3/G6 — the `CloudAware*` wrappers route between local and cloud per stage with no visible boundary, even though that boundary is the whole local-first value proposition.

---

## Validation log — backstage & support lanes (verified against source)

Skill step 9: validate the invisible lanes with the team that owns them. Here the owner is the code. Each item was checked against the cited file. **✅ confirmed · ✏️ corrected · ➕ added (was missing in v1).**

### Pipeline orchestration & stage order
- ✏️ **Canonical run order is 7 stages, not 8.** `TrackdubDubbingEngine.DefaultStageOrder` (Sdk): **Separation → VAD → Diarization → ASR → Translation → TTS → Export**. Diarization is deliberately before ASR so speaker labels exist when `SpeakerAssignmentAndPersistenceStage` persists the transcript.
- ✏️ **Speech prep/enhancement, qwen2.5 text-refinement, lip-sync, and mixing are NOT canonical top-level stages.** Handlers exist (`SpeechAudioPreparationStageHandler`, `LipSyncStageHandler`, `MixPlanBuilder`) but are sub-steps / separate workflows, absent from `DefaultStageOrder`. Mixing folds into **Export** in the headless path; the app adds an interactive **Preview** layer (`PreviewMixWorkflow`) on top.
- ✅ Transcript sub-pipeline order confirmed: `TranscriptGenerationService` builds VAD → Diarization → ASR → SpeakerAssignment via `TranscriptPipelineBuilder.AddStage(...)`.
- ✅ Stages run **sequentially** (`TranscriptGenerationPipeline.ExecuteAsync` foreach; `CloudAwareTranslationEngine` is explicitly "not thread-safe, sequential pipeline use"). Confirms G4's long blind wait.
- ➕ **SDK vs App:** the 7-stage canonical order is the *headless* `TrackdubDubbingEngine` truth. The Avalonia first-dub interleaves user steps (review/voice/preview) between Translation and Export using the **same** `workspace` workflows (`RunTranscriptStageAsync`, `GenerateTranslationAsync`, `GenerateTtsForAllSpeakersAsync`, `ExportAsync`).

### Failure / skip / resume semantics  *(missing from v1)*
- ➕ **Prerequisite gating:** only **VAD and ASR** block downstream (`PrerequisiteStages`). Their failure → later stages `Skipped / PREREQUISITE_FAILED`. Separation/Diarization/Translation/TTS failing yields **PartialSuccess**, not total failure.
- ➕ **Run-level resume:** before each stage, `HasValidExistingArtifactsAsync` → `StageArtifactResumeEvaluator.CanResumeStage` skips stages whose artifacts match the current run's `ExecutionSnapshot` (`EXISTING_ARTIFACTS_VALID`). This — plus the immutable `ExecutionSnapshot` captured at run start — is the real "preserve artifacts" guarantee.
- ✏️ **`ArtifactWriteTransaction` is narrower than v1 stated:** it is an atomic temp→commit wrapper — an uncommitted (failed) write deletes the temp file and never touches `FinalPath`. Preservation-on-failure is a *consequence* of atomic commit, not an explicit preserve step.
- ✅ Structured reasons confirmed: `StageOutcome.ReasonCode` (CANCELLED / PREREQUISITE_FAILED / STAGE_FAILED / EXISTING_ARTIFACTS_VALID) + `PipelineDegradationRecord` (code, detail, recommended action).

### Readiness / pre-flight  *(repositioned — reinforces G5)*
- ✏️ **Pre-flight runs inside the run, after the user presses Run.** `RunPreFlightChecksAsync` executes once at the top of `ExecuteAsync`, before the stage loop — **not** a Configure-time gate. (v1 placed `PipelinePreFlightChecker` in Phase 3; corrected.)
- ➕ **Auto-downloadable VAD/ASR/Diarization models do NOT fail pre-flight — they are provisioned *mid-stage*** (`TrackdubDubbingEngine.RunPreFlightChecksAsync`, the `CanAutoDownload && stageProvisionedDuringExecution` branch). A first run can therefore stall on model downloads inside a stage with no earlier warning. Direct evidence for G4.
- ✅ `RuntimePlannerPreFlightChecker` plans model + execution provider per stage; throws `RequiredModelNotAvailableException` when the plan is `Blocked` (no compatible model/EP) or `DownloadRequired`.

### Cloud routing  *(sharpens G3)*
- ✅/✏️ **Local-vs-cloud is chosen per stage purely by `PreferredModelAlias`** (the model picker). `CloudAwareTranslationEngine` routes to DeepL/OpenAI/Gemini when the alias matches, else local; `CloudAwareTtsEngine` mirrors this for ElevenLabs/OpenAI. There is **no separate cloud-egress consent**, and pre-flight even *skips* the local-model check for cloud aliases (`ShouldSkipModelPreFlight` → DeepL). The egress boundary is invisible and rides on a dropdown.

### FFmpeg / native support  *(confirms G2)*
- ✅ `FfmpegToolResolver` resolves in order: explicit path → `TRACKDUB_FFMPEG_PATH` → PATH → common roots (winget/choco/Program Files) → installer payload → **auto-download (last resort, `allowAutoDownload=true`)**. If all fail it throws `InvalidOperationException` deep in the first media op. Resilient on dev machines; on a clean end-user box the whole pipeline hinges on that one auto-download — a real single point of failure.

### Round 2 — mixing / preview DSP, model-setup flow, cloud TTS

- ✅ **Cloud TTS mirrors translation routing.** `CloudAwareTtsEngine` routes by `request.Options.NormalizedPreferredModelAlias`: ElevenLabs / OpenAI / **Google** → cloud, else local. Cloud TTS is **3 providers** (v1 said 2). Same no-consent alias mechanism as G3 — confirms G6.
- ✏️ **Model setup is an interactive decision loop, partly front-loaded.** `RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync`: per request, check `GetRequiredModelStatusAsync`; if missing, loop on `callbacks.ResolveDecisionAsync` → **Cancel / Download / Import / SkipOptionalStage** (this callback *is* the modal dialog). `RuntimeModelSetupCoordinator.EnsureImportModelsAvailableAsync` runs it at **import**; per-stage `Ensure*Available` on demand. Only **Separation** is skippable (`IsOptionalRuntimeStage`). → refines G5 (now Med).
- ✏️ **Preview/mix DSP is richer and different from v1.** `PreviewRangeRenderer.RenderAsync`: source-gain + **ducking** (`FillDuckingGains` from `MixPlan.DuckingRegions`) + dubbed-speech-gain; per-clip **room-tone timbre-polish** via `RoomToneConvolver.TryApply(dryTake, 0.3s source pre-roll)` **gated by `ApplyTimbrePolish`**; optional **pan-restore** from original L/R RMS; full **multichannel→stereo downmix** (channel-mask aware, 5.1/7.1). It renders a **range only** and is a tracked `StageNames.PreviewMix` stage-run with atomic artifact commit + fingerprint.
- ✏️ **Loudness normalization is NOT in the mix renderer** — it's `FfmpegLoudnessNormalizer` (Media/Loudness), applied at audio extraction. v1 wrongly placed it in Preview backstage.
- ✏️ **Timing reconciliation is a TTS-stage concern, not Export.** `WsolaPhonemeStretchService` / `AudioTimeStretchService` are driven by `TtsOrchestrationService` + `StartTtsStageHandler` to fit each take to its segment duration. The renderer only **resamples** (sample-rate) and **time-places** takes (`MixTakeIntoOutput`) — no stretching. Export muxes the already-fitted mix. v1 mislabeled this as an Export step.
- ✅ Preview is a **first-class tracked stage** (`StageNames.PreviewMix`, `StageRunHelper` Start/Complete/Fail/Cancel), not a transient UI render.
- ⏳ **Not traced:** `ExportStageHandler` exact call graph (also references a stretch service — possible final-fit), and `RoomToneFallbackImpulse` usage when no pre-roll exists. Low risk; flagged for honesty.

### Net effect
Corrections **strengthen most gaps and honestly soften one.** Round 1: G2/G3/G4 each gained a code-cited mechanism; two backstage truths were missing (prerequisite gating, run-level resume). Round 2: **G5 softened High→Med** (import front-loads setup), **G6 confirmed** (3 cloud TTS providers, no consent), and the mix/timing lanes were corrected (loudness ≠ preview; WSOLA = TTS, not Export). Where the as-is tables and this log differ, **this log is authoritative.**

---

## Notes on accuracy / scope

- **Local-first holds by default.** Translation, ASR, TTS all have bundled local ONNX engines. The cloud tier (OpenAI / Gemini / DeepL / ElevenLabs) is **opt-in, BYO-key, and genuinely wired** via `CloudAware*` engines in `Trackdub.Composition` + `ApiKeyStore` / `EnvironmentCloudApiKeyProvider` — not a dead dialog. That makes the local↔cloud boundary a first-class blueprint concern, not a footnote.
- **`Trackdub.Application/Services/TranslationService` is a legacy stub** (returns `[Translated: …]`). The live path is `TranslationOrchestrationService` → `CloudAwareTranslationLanguageRouter` → local opus-mt/madlad/phi or cloud. Worth deleting/quarantining to avoid future confusion.
- **Scope held to first-dub.** First-run *model governance* (checksum/license/commercial gating, EP install, Olive optimization) is its own candidate blueprint — it appears here only as the Support lane of Phase 4, deliberately not expanded.
- **Co-create next:** validate the Backstage and Support lanes against the actual stage handlers with whoever owns the pipeline; backstage steps are the lane most often missing from a first draft.
