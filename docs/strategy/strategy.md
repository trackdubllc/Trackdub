# Trackdub Long-Term Roadmap

This document is the long-range roadmap for Trackdub. It replaces the old mega `MILESTONE.md` role without trying to make every long-term item an immediate execution priority.

Use `MILESTONE.md` for the active near-term queue. Use this file for long-term direction, feature arcs, model-provider strategy, and future milestone shape.

Use `docs/BACKLOG.md` for P-tier execution items that may temporarily outrank milestone order.

**Last reviewed:** 2026-06-12 (against current source, tests, and `docs/BACKLOG.md`).

## Source of truth order

1. Current source code and tests.
2. Explicit task instructions, active GitHub issues, and review threads.
3. `AGENT_CONTEXT.md`.
4. `AGENTS.md`, `CLAUDE.md`, or `GEMINI.md`.
5. `MILESTONE.md` for near-term priority order.
6. `docs/BACKLOG.md` for P-tier bugs and UX debt.
7. This long-term roadmap.
8. Archived milestone completion notes and old roadmaps.

Historical plans are evidence, not binding implementation truth. When a historical milestone conflicts with current code, inspect the source and preserve the architecture unless the task explicitly asks for migration.

## Product spine

Trackdub is a cross-platform, local-first AI dubbing workstation for Windows, macOS, and Linux. The active desktop shell is **Avalonia** (`Trackdub.App.Avalonia`), multi-targeting portable `net10.0` and Windows WinML/DirectML legs. The desired product spine is:

```text
media ingest
  -> audio preparation
  -> optional speech/noise split or dialogue/stem separation
  -> VAD
  -> diarization
  -> ASR
  -> transcript confidence review
  -> translation
  -> glossary / terminology hints
  -> speaker and voice assignment
  -> TTS
  -> timing reconciliation
  -> optional audio-level lip alignment
  -> preview mix
  -> export
  -> optional visual dubbing / generated portrait branches
```

The product should feel like a reliable workstation, not a magic demo. The core engineering rule remains: do not fake readiness. Provider registration, runtime availability, model cache state, checksum status, license status, user opt-in, stage skip, stage failure, and usable output are separate states.

## Roadmap philosophy

Trackdub can jump around milestones when useful, but each milestone must still reduce uncertainty or deliver a testable slice. The long-term roadmap should support exploratory work without letting experiments contaminate stable local-first workflows.

Each substantial feature should have:

* a manifest-gated provider model,
* fake-backed application tests,
* artifact preservation,
* explicit provenance,
* visible fallback or skip reasons,
* a non-destructive failure path,
* and user-facing wording that matches what the model actually does.

A speech enhancer is not a stem separator. A two-speaker separator is not a music-removal model. A premium vendor SDK is not an open local default. Keep the nouns sharp so the architecture does not become soup.

## Model and provider governance

Every real model or provider route must be manifest-driven. A provider may be local ONNX, native SDK, cloud API, external binary, or future training artifact, but it still needs the same governance shape:

```json
{
  "model\_id": "string",
  "provider\_id": "string",
  "task": "asr | translation | tts | separation | speech\_enhancement | overlap\_rescue | forced\_alignment | lip\_synthesis | portrait\_animation",
  "engine\_family": "string",
  "source\_url": "https://...",
  "format": "onnx | native-sdk | cloud-api | external-binary | training-dataset | other",
  "expected\_runtime": "onnxruntime-cpu | onnxruntime-directml | windows-ml | native-sdk | cloud | other",
  "input\_contract": "description",
  "output\_contract": "description",
  "commercial\_allowed": true,
  "noncommercial\_allowed": true,
  "experimental": false,
  "human\_reviewed": false,
  "checksum": "sha256:...",
  "quality\_caveats": \[],
  "known\_failure\_modes": \[]
}
```

Do not allow roadmap text to become implementation truth. The implementation source of truth remains the manifest, runtime planner, DI registration, tests, and application stage code.

## Audio separation and cleanup strategy

The current near-term audio strategy is not “find one perfect free separator.” The realistic strategy is multiple lanes with honest labels.

|Priority|Candidate|Trackdub lane|Why it matters|Implementation posture|What not to claim|
|-:|-|-|-|-|-|
|1|speechbrain/sepformer-libri2mix (bundled as `tonythethompson/sepformer-whamr16k-onnx` in manifest)|Overlap speech rescue|Useful for two-speaker overlap mitigation, which is a different product capability from background separation. Could improve ASR/diarization on overlapping dialogue regions.|Manifest + model manager inventory exist; dedicated `overlap-rescue` stage and UI lane **not** wired. Keep out of the main stem separator path at first.|Do not call its outputs stable speakers. Do not use it as a music/background separator.|
|2|Weya Hush|Speech enhancement|Potentially useful for ASR cleanup and background-speaker suppression. It overlaps with existing enhancement work and should be lower priority than the current speech-enhancement lane.|Add as an optional `speech-enhancement` provider only if current enhancement is insufficient.|Do not route it as stem separation. Do not use it to generate ambiance.|
|3|Auphonic Speech Isolation|Optional cloud speech cleanup|Good benchmark and later cloud-provider lane. Useful once Trackdub has cloud provider abstractions and user consent flows.|Back burner until cloud provider architecture exists.|Do not add cloud upload as a hidden fallback.|
|4|AudioShake Local SDK|Premium commercial separation provider|Likely the best long-term high-quality provider for dialogue/music/instrument separation, but it is premium-gated and cannot be tested without vendor access.|Back burner until the app shape is stronger, or funding/user demand justifies SDK access. Treat as `audioshake-local` native SDK provider.|Do not prioritize before a testable app exists. Do not make it a free/open default.|
|5|GCX / Sonovox / rights-cleared training data|First-party model training route|Long-term moat if Trackdub succeeds and needs owned commercial-safe separation models.|Watch closely. Use later for custom dataset licensing, benchmarks, and model training.|Do not schedule as near-term implementation. Dataset access is not a model.|

### Audio model lane definitions

#### Speech/noise split

Purpose: generate a speech-forward artifact and a background/noise residual that can feed ASR, preview, or a rough commercial-safe audio bed.

Near-term candidate: none currently implemented. The roadmap should not name a specific model until it is manifest-gated and adapter-tested.

Expected outputs:

```text
speech.wav      -> used as vocals/dialogue candidate
noise.wav       -> used as rough ambiance/background candidate
metadata        -> must state speech/noise split, not cinematic stem separation
```

#### Dialogue enhancement

Purpose: improve ASR and intelligibility by suppressing noise or competing speech.

Candidate: Hush.

Expected outputs:

```text
enhanced\_speech.wav
metadata -> speech enhancement provider, no ambiance claim
```

This belongs closer to the existing speech-enhancement lane than the stem-separation lane.

#### Overlap speech rescue

Purpose: handle overlapping speech regions where diarization/ASR struggle.

Candidate: SepFormer Libri2Mix.

Expected outputs:

```text
overlap\_source\_1.wav
overlap\_source\_2.wav
repair candidates for re-ASR or manual review
```

This should run on suspected overlap regions, not full movies. Source identity can flip between chunks, so the UI should not label outputs as stable speakers unless a separate identity-stitching step proves it.

#### Premium dialogue/music separation

Purpose: high-quality commercial separation of dialogue/vocals, background, music, and effects.

Candidate: AudioShake Local SDK.

Expected outputs depend on the licensed SDK/model. This should be a premium or pro-gated provider lane after the app has enough shape to justify vendor access.

#### Cloud speech isolation

Purpose: optional cloud-based cleanup or benchmark lane.

Candidate: Auphonic.

This requires explicit user consent, provider credentials, upload status, cost display, and project metadata stating that cloud processing was used.

#### First-party trained separator

Purpose: owned long-term separation quality and commercial safety.

Candidate inputs: GCX, Sonovox, other rights-cleared datasets.

This is a future ML program, not a near-term feature. It belongs after product validation.

## Long-term milestone map

This roadmap keeps historical milestone numbers where useful, but it no longer treats every historical item as active priority. The active priority order lives in `MILESTONE.md`.

### M0-M7: Foundation and first vertical slices

Status: historical foundation.

These milestones establish repository structure, model manifest policy, SQLite project spine, media ingest, runtime planning, transcript generation, and translation. They should remain reference material for architecture and test expectations, not active roadmap churn.

Key durable principles from this arc:

* use real model routes with fakes for tests,
* keep manifests ahead of execution,
* store project artifacts durably,
* record stage runs,
* expose failure states clearly,
* avoid UI-owned pipeline truth.

### M8-M16: Workstation spine

Status: mostly implemented; treat current source as truth.

This arc covers video playback (libmpv primary, LibVLC fallback), segment editing, translation expansion, diarization, transcript confidence, Kokoro TTS, timing reconciliation, stem/speech separation (Spleeter bundled), preview mix, voice cloning, export, and hardware acceleration.

Long-term intent:

* video and waveform context should support every downstream review stage (waveform scaffolding exists; timeline-embedded waveforms remain backlog **P4-1**),
* diarization and speaker assignment should precede voice assignment,
* TTS takes should be immutable artifacts,
* preview and export should share mix-plan semantics,
* export should preserve provenance,
* hardware acceleration should stay honest about provider availability and model compatibility.

### M16a: Expanded hardware acceleration and runtime routing

Status: long-term architecture seam.

Primary Windows route is **Windows ML** (ONNX Runtime integration, catalog EP registration, explicit device selection). Prefer catalog execution providers where model-compatible (for example TensorRT RTX on NVIDIA RTX, MIGraphX on AMD). **DirectML** is supported legacy GPU fallback—not the forward acceleration centerpiece. Linux/macOS use native ORT paths (CUDA/TensorRT, CoreML) per platform policy. CUDA on Windows exists only when advanced settings enable native ORT providers and must not create fake readiness.

Acceptance posture:

* runtime route selection is manifest and smoke-test driven,
* Windows ML bootstrap code stays in the concrete inference layer,
* planner state distinguishes registered providers from verified runnable model paths,
* user-visible fallbacks explain what happened.
* **Phase 2 (2026-05-23):** stage allow-lists aligned with milestone probe order; bundled ONNX `expected\_runtime` uses `windows-ml|onnxruntime-migraphx|onnxruntime-directml` (see [ADR-0002](docs/adr/ADR-0002-windows-ml-provider-strategy.md)).

### M17: Glossary and terminology

Status: partial; finish after pipeline reliability and M21 polish.

Goal: make terminology consistent across translation by supporting project and global glossaries.

Already present according to current source:

* glossary domain/storage pieces,
* SQLite repository/migration,
* CSV import/conflict service,
* term matching,
* CJK/Arabic analyzers,
* translation hints,
* **project-scoped Avalonia glossary panel** (add/delete/load by language pair).

Remaining long-term completion target:

* CSV import UI,
* global glossary persistence UX,
* translated-segment highlight UI,
* project/global scope workflow,
* review-safe conflict display.

### M18: Diagnostics bundle

Status: completed after the older partial review.

Long-term role: keep diagnostics local by default, redact sensitive data, and make bug reports inspectable without silently uploading project media.

Future enhancements can add richer stage timelines and exportable provider/runtime audits, but M18 should not be treated as open unless current source shows regression.

### M19: Hardware profiler and preset recommendation

Status: largely implemented; optional SQLite history migration remains.

Goal: benchmark real local hardware against actual pipeline workloads and recommend a quality preset.

Already present according to current source:

* runtime route selection,
* provider discovery/planner integration,
* hardware override settings,
* Hardware Profiler tab with four stage benchmark scenarios (VAD, ASR, translation, TTS),
* JSON evidence under `%LOCALAPPDATA%\Trackdub\hardware-profiler\`,
* quality preset thresholds and driver/fingerprint invalidation,
* `IRuntimePlanningPreferences` + `RuntimePlanningPreferencesService`,
* `StageRunHelper` persists `BenchmarkEvidenceId` on stage runs when profiler evidence is fresh,
* user-facing preset override in studio settings.

Remaining completion target:

* optional: migrate profiler JSON history into `IBenchmarkRepository` / SQLite `BenchmarkRuns`,
* repeat translation matrix smoke when opus ONNX pairs are present on disk.

### M20: Performance profiling and optimization

Status: partial; should be finished after pipeline reliability and M19 cleanup.

Goal: establish measurable performance baselines before broad feature expansion.

Already present according to review evidence:

* `InferenceSessionPool`,
* `SessionPoolKey`,
* session pool tests,
* warm session factory use in multiple engines,
* benchmark harness pieces.

Remaining completion target:

* `docs/performance/profiling-report.md`,
* SQLite `EXPLAIN QUERY PLAN` audit,
* startup target evidence,
* memory ceiling test,
* Avalonia render/frame budget evidence (headless UI tests under `Trackdub.UI.Tests`),
* export throughput target,
* complete baseline evidence.

### M20a: Full pipeline benchmark suite

Status: long-term validation layer.

Goal: benchmark representative end-to-end local workflows so runtime regressions are visible before release.

This should build on M19 and M20, not precede them. The benchmark suite should run against small representative projects, collect stage timings, record model/provider choices, and produce comparable reports.

### M21: Model manager and downloader

Status: **largely implemented**; remaining polish and first-run integration.

Goal: close the gap between “manifest says model exists” and “the user can install, verify, and use it without manual file spelunking.”

Implemented capability (current source):

* model inventory UI (`ModelManagerViewModel`, Settings Local Models tab),
* model manifest display and stage grouping,
* download / repair / uninstall via `IModelDownloadOrchestrator`,
* checksum verification and cache inventory updates,
* license and commercial-safe status display,
* clear states for missing, downloading, corrupt, installed, blocked, and ready,
* headless CLI/TUI models commands,
* composition tests and P0-5 DI regression coverage.

Remaining polish:

* first-startup gate behavior vs `ShowLocalModelsAtStartup` policy,
* consistent auto-provision before stages that require bundled ONNX,
* clean-machine verification of storage/repair flows.

This milestone is no longer greenfield, but cache truth must stay honest because every serious future model route depends on it: SepFormer overlap rescue, lip alignment, visual dubbing, vendor providers, and future training artifacts.

### M22-M26: Consolidated visual dubbing arc

The old separate `V22-V26` visual dubbing plan is now consolidated into the long-term roadmap as M22-M26.

From this point forward:

* M22-M26 refer to the visual/audio-accurate dubbing arc.
* Older long-term items that used to occupy M24-M26, such as expanded language support, UI localization, and cross-platform shell, remain important but move to the later product expansion backlog instead of competing for the same numbers.

#### M22: Phoneme-aligned lip sync, audio-level

Goal: align dubbed TTS phoneme/viseme timing to the original speaker’s mouth cadence without modifying video frames.

Status: **wired and real-model verified (2026-06-12).** Source includes `IForcedAligner`,
`IForcedAlignerAdapter` (now with a `SupportsPhonemeTimings` capability flag), `RoutedForcedAligner`,
ONNX aligner routes (`Wav2Vec2CtcForcedAligner`, `QwenForcedAligner`), `LipSyncStageHandler`,
`LipSyncWorkflow`, domain records, fake-backed `LipSyncStageHandlerTests`, SQLite persistence, and
Avalonia segment-state chips. The six known gaps are closed:

* **routing** — `RoutedForcedAligner` now honors `PreferredModelAlias` and a `RequirePhonemeTimings`
  capability gate, so lip-sync never lands on the word-level Qwen aligner; wav2vec2 is registered first;
* **composition** — fixed the wrong model id (`…-ft-int8-onnx` → manifest `wav2vec2-lv60-espeak-cv-ft-onnx`)
  and resolve-to-cache-root so `IsAvailable` flips after a download with no restart;
* **manifest** — wav2vec2 entry now downloads `vocab.json` with sha256 hashes for all three files;
* **runtime planning** — added `RuntimeStage.LipSync` to `StageRuntimeRequirementsCatalog`;
* **transcript split** — source alignment uses original `TranscriptSegment` text, TTS alignment uses
  translated text (`SourceSegmentTranscriptMap`); missing source text skips cleanly to `Partial`;
* **real model** — `wav2vec2-lv60-espeak-cv-ft-onnx` downloaded via the product CLI, `IsAvailable` true,
  one WAV → non-empty phonemes (`Wav2Vec2CtcForcedAlignerIntegrationTests`), and the full routed stage
  runs end-to-end with the real model (`LipSyncRealAlignerIntegrationTests`).

Position:

```text
TTS generation
  -> TTS timing reconciliation
  -> phoneme-aligned audio lip sync
  -> preview mix / export
```

Core deliverables:

* `IForcedAligner`,
* word and phoneme timing records,
* phoneme inventory mapping,
* CTC/phoneme aligner route,
* phoneme timing planner,
* conservative stretch service,
* per-segment lip-sync status,
* fake aligner and fake stretch service,
* manifest gates for real aligners.

Commercial candidate lane:

* ONNX CTC phoneme aligner based on a wav2vec2/eSpeak-style phoneme model.

Non-commercial/reference lane:

* MMS forced alignment or similar reference aligner, blocked in commercial mode if required.

Acceptance posture:

* fake-backed architecture lands first,
* real aligner does not run without manifest, cache, checksum, and runtime readiness,
* low-confidence alignment skips and preserves the original TTS take,
* duration matching alone is not treated as quality.

#### M23: Video lip synthesis, original-footage repair

Goal: repair mouth motion in original footage while keeping the source video as the authority.

This is original-footage repair, not generated portrait animation.

Core deliverables:

* `ILipSynthesisEngine`,
* face/mouth region detection seam,
* speaker-turn processing rules,
* face quality guards,
* per-segment render status,
* artifact-preserving patched video outputs,
* fake-backed video synthesis tests before any real provider.

Rules:

* never overwrite source video,
* preserve original video as authority,
* skip unsafe face regions,
* mark outputs experimental unless quality and license gates are proven.

#### M24: Portrait animation provider API

Goal: support generated talking portrait modes through a separate provider API.

This is not original-footage repair. It creates a new generated performance branch from an identity source and audio.

Core deliverables:

* `IPortraitAnimationEngine`,
* performance graph concept,
* generated portrait artifact type,
* provider capability flags,
* fake portrait provider,
* clear UI separation from original-footage lip repair.

#### M25: Avatar identity packs and reusable speaker identities

Goal: manage reusable identity packs for generated portrait workflows.

Core deliverables:

* identity pack records,
* provenance fields,
* consent and rights metadata,
* speaker-to-identity assignment,
* import/export workflow,
* revocation/deletion behavior,
* contamination metadata when identity sources are restricted.

#### M26: Low-latency portrait preview and realtime-oriented rendering

Goal: explore realtime or low-latency generated portrait preview modes.

This is experimental only until proven.

Core deliverables:

* streaming-oriented provider seam,
* preview quality levels,
* latency budget reporting,
* fallback to offline generation,
* clear “preview is not final export” semantics.

### Later product expansion backlog

These items are still important but should not crowd the immediate audio/model/lip-dubbing priorities.

#### Expanded language support

Goal: expand translation/TTS coverage across more language pairs with honest routing and quality warnings.

This belongs after the model manager and core audio/visual workflow are stable. Translation coverage expansion should not outrun TTS, alignment, or subtitle/export support.

#### UI localization

Goal: localize the Trackdub app UI.

This is product polish and distribution readiness. It should come after the core workflow stabilizes enough that strings are not constantly churned.

#### Cross-platform shell

Status: **in progress** — Avalonia is the active shell on Windows, macOS, and Linux.

Goal: keep both portable (`net10.0`) and Windows WinML/DirectML target legs healthy while preserving honest readiness per platform.

Remaining work: parity audits for playback, inference EP registration, and shell UX; avoid reintroducing WinUI-only paths. Backend boundaries should continue to avoid unnecessary platform-specific UI leakage.

#### Timeline and editor expansion

Goal: richer timeline editing, per-word overlays, take lanes, mix lanes, and repair workflows.

This should follow stable playback, waveform, subtitles, TTS takes, mix preview, and artifact provenance.

#### Media bin and multi-clip projects

Goal: support project bins and multiple clips.

This changes project scope and should wait until single-clip workflows are boring.

#### Batch export and queue

Goal: queue multiple exports, regenerate stale stages, and run long jobs predictably.

This belongs after export and model manager are robust.

#### Cloud inference providers

Goal: add optional cloud providers such as Auphonic or future hosted translation/TTS/separation services.

Cloud providers require explicit consent, cost display, provider credentials, upload status, privacy metadata, and no hidden fallback.

#### UX and accessibility polish

Goal: make the workstation easier to use without hiding technical truth.

This includes keyboard workflows, status clarity, screen reader support, color contrast, and project error recovery.

#### Commercial lane status and licensing UI

Goal: give users clear visibility into whether a project uses only commercial-safe models (all providers in the commercial lane).

This should include model/provider route history, contamination metadata, blocked export states, attribution display, and user-facing license explanations.

#### Packaging and clean-machine install

Goal: install Trackdub on a clean Windows machine without manual runtime archaeology.

This depends on model manager, FFmpeg/runtime handling, native binaries, app settings, logs, and update strategy.

#### Public alpha

Goal: release a constrained but honest version of Trackdub.

Minimum alpha should include: media ingest, transcript, translation, TTS, preview, export, model manager, diagnostics, and at least one commercial-safe audio cleanup/separation baseline.

#### Beta and monetization readiness

Goal: prepare the product for paid tiers without locking away core offline usefulness.

Free tier should remain useful. Premium lanes can include higher-end provider SDKs, batch scale, cloud providers, advanced visual dubbing, and pro export workflows.

#### Auto-update

Goal: keep app updates reliable, signed, reversible, and separate from model downloads.

Model updates and app updates should not be conflated.

## Current strategic sequence

The near-term execution sequence is intentionally narrower than the long-term ambition:

```text
0. Pipeline reliability and honest execution (docs/BACKLOG.md P0)
1. Model manager polish and first-run integration (M21 largely done)
2. Finish partial historical gaps: M17, M20 (M19 largely done)
3. Finish and wire M22 audio-level phoneme alignment (architecture verified)
4. Reassess SepFormer overlap rescue after M22 clarifies the audio pipeline
5. Continue visual dubbing (M23+) if fake-backed architecture and model gates hold
```

This sequence keeps Trackdub from trying to solve every audio problem at once. Pipeline honesty comes first, then model-manager polish, then M22 timing truth. SepFormer waits until the app has a defined place for overlap repair (manifest inventory exists; stage lane does not).

## Agent checklist for long-term work

Before implementing a long-term item, an agent should answer:

* Which stage does this actually belong to?
* Is it speech enhancement, speech/noise split, stem separation, overlap rescue, lip alignment, lip synthesis, or portrait animation?
* Does the manifest already have the provider/model route?
* Does the model manager know how to install/verify it?
* Does the manifest need a new lane, commercial_allowed, or commercial_use_verified entry for this model?
* Are fake-backed application tests in place?
* Are artifacts committed atomically with provenance?
* Does failure preserve prior usable artifacts?
* Does the UI show readiness truthfully?
* Is this item actually in the near-term `MILESTONE.md`, or is it long-term backlog?

## Final positioning

Trackdub should become a local-first dubbing workstation with optional premium/cloud extensions, not a fragile stack of model experiments. The long-term roadmap can be huge. The near-term path must stay narrow enough to ship.

# Trackdub V22-V26 Roadmap: Audio-Accurate Dubbing, Lip Synthesis, and Portrait Animation

This document is the detailed handoff roadmap for Trackdub milestones V22-V26. It should be used together with the root `AGENT_CONTEXT.md` file. `AGENT_CONTEXT.md` is the operating contract for agents; this roadmap describes the planned feature slices.

Agents must read `AGENT_CONTEXT.md` before implementing any item in this file. If this roadmap appears to conflict with current repository code, inspect the source and preserve the existing architecture unless the task explicitly asks for a migration. Do not invent a parallel pipeline because it looks easier in isolation.

## Product framing

Trackdub is a Windows-first, local-first desktop application for AI-powered video dubbing. It loads local media, generates timed transcripts, translates dialogue, assigns voices to speakers, produces dubbed speech, reconciles timing, and lets users preview or export the result in context.

The product is not a research notebook with a UI wrapper. It is a media workstation. Every milestone must preserve low setup friction, resumable execution, honest readiness, model manifests, artifact preservation, clear fallback behavior, and explicit user-visible status.

The roadmap from V22 to V26 should evolve Trackdub in layers:

| Milestone | Core job | Video authority | Model risk | Product posture |
|---|---|---|---|---|
| V22 | Align dubbed audio at phoneme/viseme timing level | Original video untouched | Moderate | Production-oriented |
| V23 | Repair mouth motion in original footage | Original video remains authoritative | High | Architecture-first, experimental real provider |
| V24 | Generate a new talking portrait from an identity source | Generated video branch | High | Provider API first |
| V25 | Manage reusable avatar/identity packs | Identity/provenance layer | Medium-high | Governance and workflow |
| V26 | Explore low-latency portrait preview | Generated preview branch | Very high | Experimental only |

The strategic distinction is non-negotiable:

- V23 is **original-footage repair**. It modifies detected mouth/face regions in the source video while preserving the original clip as the authority.
- V24-V26 are **generated-performance modes**. They synthesize a new portrait or avatar performance from an identity source and audio.

Do not collapse these into one abstraction. `ILipSynthesisEngine` and `IPortraitAnimationEngine` should exist for different reasons.

## Model support lanes

Every milestone in this roadmap uses three explicit model lanes.

### Commercial lane

The commercial lane is the default product-safe lane. A provider may run in commercial mode only when the model manifest proves all required facts.

Commercial-lane requirements:

- Code license verified.
- Weight license verified.
- Dependency licenses reviewed.
- Model-card terms reviewed.
- Training-data restrictions noted where known.
- Source URL recorded.
- Checksums verified.
- `commercial_allowed: true`.
- `human_reviewed: true`.
- No known non-commercial training-data restriction that blocks product use.

Commercial mode should prefer boring, durable, license-clean providers over exciting research models. Boring is good here. The default commercial lane should be the sturdy bridge, not the neon rope swing.

### Non-commercial lane

The non-commercial lane is opt-in. It exists for research models that may be technically strong but are not safe for commercial workflows.

Non-commercial-lane requirements:

- `commercial_allowed: false`.
- `noncommercial_allowed: true`.
- Clear user warning.
- Project contamination metadata.
- Commercial-mode export blocked or clearly marked unavailable.
- The project must remember that a non-commercial provider was used.

A user must not be able to toggle back to commercial mode after using a non-commercial provider and accidentally launder the project state. Once a project uses a non-commercial model, store that fact in project metadata.

### Experimental lane

The experimental lane is for unstable, GPU-heavy, Python/CUDA-first, partially verified, partially exported, or non-ONNX providers.

Experimental does not automatically mean non-commercial. A provider can be:

- commercial and stable,
- commercial-candidate but experimental-runtime,
- non-commercial and stable enough for research use,
- non-commercial and experimental,
- blocked entirely.

Experimental-lane requirements:

- Visible experimental label.
- Runtime checks before work begins.
- Capability flags.
- Clear skip/failure reasons.
- No fake readiness.
- No blocking of stable commercial paths.
- No silent fallback to a different model lane.

## Cross-cutting model governance

Every real model provider must have a manifest entry before it can run. A repository license is not enough. Code license, pretrained weights, dependency models, model-card terms, training datasets, and sample/test datasets can all have different restrictions.

A useful manifest should include at least:

```json
{
  "model_id": "string",
  "provider_id": "string",
  "task": "forced_alignment | lip_synthesis | portrait_animation | identity | face_detection | landmarking | pose_estimation",
  "source_url": "https://...",
  "model_card_url": "https://...",
  "code_license": "Apache-2.0 | MIT | BSD-3-Clause | ...",
  "weight_license": "Apache-2.0 | MIT | OpenRAIL++ | CC-BY-NC-4.0 | ...",
  "dependency_licenses": [
    {
      "name": "dependency name",
      "license": "license id",
      "source_url": "https://...",
      "commercial_allowed": true
    }
  ],
  "training_data_notes": "Known training-data constraints or unknowns.",
  "commercial_allowed": true,
  "noncommercial_allowed": true,
  "experimental": false,
  "human_reviewed": true,
  "checksum": "sha256:...",
  "format": "onnx | pytorch | external-python-provider | other",
  "expected_runtime": "onnxruntime-cpu | onnxruntime-directml | python-cuda | ffmpeg | other",
  "supported_hardware": ["cpu", "directml", "cuda"],
  "minimum_vram_mb": 0,
  "recommended_vram_mb": 0,
  "input_contract": "Description of expected inputs.",
  "output_contract": "Description of outputs.",
  "quality_caveats": ["Known caveat."],
  "known_failure_modes": ["Known failure."],
  "blocked_reason": null
}
```

The manifest gate should be enforced before stage execution. The UI may show why a provider is unavailable, but it must not let unavailable providers appear ready.

## Cross-cutting readiness rules

Do not collapse readiness into a single boolean. The app must distinguish:

- provider registered,
- runtime installed,
- external binary available,
- model manifest present,
- model files downloaded,
- checksum verified,
- license metadata present,
- license reviewed,
- commercial mode allowed,
- hardware provider available,
- stage enabled in the immutable execution snapshot,
- prerequisites satisfied,
- stage ran,
- stage produced usable output,
- stage skipped safely,
- stage failed.

A disabled stage did not run. A skipped stage did not succeed. A provider being registered does not mean the model is installed. A repo license does not prove pretrained weights are commercially usable. A duration-matching audio file does not prove lip-sync quality.

## Cross-cutting execution rules

All new stage work must follow these rules:

- Capture an immutable execution snapshot at run start.
- Use the snapshot during the run. Do not observe mutable UI settings mid-run.
- Preserve original media and prior successful artifacts.
- Use structured status, skip reason, and failure reason values.
- Prefer partial safe output over crashing or silently degrading.
- Do not overwrite source artifacts in place.
- Record provider id, model id, runtime, stage id, source artifact ids, output artifact ids, and model lane metadata.
- Do not add real model I/O to normal fast tests.
- Add fakes before real providers.

## Project boundaries to preserve

Verify exact paths in the current repository before editing. The expected conceptual boundaries are:

- `src/Trackdub.App.Avalonia/`
  - UI, view models, status projections, configuration panels, segment lists, user commands.
  - Must not own inference or persistence.
  - Must reflect application state, not fabricate readiness.

- `src/Trackdub.Application/`
  - Pipeline orchestration, stage plans, execution snapshots, artifact routing, provider selection, license gates, application services.
  - Most stage plans belong here.

- `src/Trackdub.Inference/`
  - Provider-neutral inference interfaces and contracts.
  - No UI types.

- `src/Trackdub.Inference.Onnx/`
  - ONNX-specific implementations.
  - ONNX Runtime provider wiring.
  - Do not claim support for video synthesis ONNX until export/operator/runtime compatibility is proven.

- `tests/Trackdub.TestDoubles/`
  - Shared fakes and deterministic test doubles.
  - No real model/audio/video/network I/O unless an integration test explicitly opts in.

- `tests/*`
  - Unit, integration, architecture, and benchmark tests.
  - Normal tests should be cheap and deterministic.

---

# V22 — Phoneme-aligned lip sync, audio-level

## Goal

Align dubbed TTS phoneme and viseme timing to the original speaker’s mouth cadence without manipulating video frames. This stage should make consonant closures, vowel openings, and syllabic beats land closer to the visible source-mouth timing while preserving pitch and the main TTS take.

V22 is the timing-truth milestone. It earns better audio alignment before Trackdub touches pixels.

## Position in pipeline

V22 runs after the existing TTS timing reconciliation stage. It consumes post-atempo TTS takes and source segment audio. It produces phoneme-aligned audio takes when safe, and preserves the original post-atempo take when skipped.

Suggested ordering:

```text
source media
→ transcript / segments
→ translation
→ TTS generation
→ TTS timing reconciliation / atempo
→ V22 phoneme-aligned lip sync
→ V23 video lip synthesis
```

## Projects touched

Expected projects:

- `src/Trackdub.Inference/`
- `src/Trackdub.Inference.Onnx/`
- `src/Trackdub.Application/`
- `src/Trackdub.App.Avalonia/`
- `tests/Trackdub.TestDoubles/`
- relevant application/unit test projects

Confirm exact current project names and dependency rules before editing.

## Commercial model lane

Commercial candidate:

- `facebook/wav2vec2-lv-60-espeak-cv-ft`
- Expected license: Apache-2.0.
- Task shape: phoneme/token recognition.
- Preferred runtime: ONNX export.
- Preferred provider name: `OnnxCtcPhonemeAligner`.

Important interpretation:

This model should not be treated as a turnkey forced aligner. It emits phoneme/token logits. Trackdub owns the forced-alignment algorithm on top of those logits.

Required commercial implementation layers:

1. Source audio normalization.
2. Transcript normalization.
3. Transcript phonemization.
4. Internal phoneme inventory mapping.
5. CTC trellis/path alignment.
6. Token/phoneme interval extraction.
7. Word interval aggregation.
8. Confidence scoring.
9. Skip/fallback decisions.

Do not name the real commercial implementation simply `OnnxForcedAligner` if that hides the CTC/logits nature of the model. A clearer name is `OnnxCtcPhonemeAligner`.

Suggested commercial manifest shape:

```json
{
  "model_id": "wav2vec2-lv60-espeak-cv-ft-onnx",
  "provider_id": "onnx-ctc-phoneme-aligner",
  "task": "forced_alignment",
  "source_url": "https://huggingface.co/facebook/wav2vec2-lv-60-espeak-cv-ft",
  "code_license": "Apache-2.0",
  "weight_license": "Apache-2.0",
  "commercial_allowed": true,
  "noncommercial_allowed": true,
  "experimental": false,
  "human_reviewed": false,
  "format": "onnx",
  "expected_runtime": "onnxruntime-cpu/directml",
  "sample_rate_hz": 16000,
  "input_contract": "16 kHz mono WAV plus transcript text after normalization.",
  "output_contract": "CTC-derived word and phoneme/token intervals with confidence.",
  "quality_caveats": [
    "Model emits phoneme/token logits, not final timestamps.",
    "English timing quality depends on phonemizer and inventory mapping.",
    "Fast speech and overlapping speakers degrade confidence."
  ],
  "known_failure_modes": [
    "Transcript/audio mismatch",
    "Overlapping speech",
    "Unmapped phonemes",
    "Low CTC path confidence"
  ]
}
```

## Non-commercial model lane

Non-commercial candidate:

- MMS forced alignment ONNX, commonly referred to as MMS_FA.
- Expected license: CC-BY-NC-4.0.
- Must be blocked in commercial mode.
- Useful as a stronger research/reference aligner for comparison, quality evaluation, and non-commercial projects.

Suggested non-commercial manifest shape:

```json
{
  "model_id": "mms-fa-onnx-noncommercial",
  "provider_id": "onnx-mms-forced-aligner",
  "task": "forced_alignment",
  "weight_license": "CC-BY-NC-4.0",
  "commercial_allowed": false,
  "noncommercial_allowed": true,
  "experimental": false,
  "human_reviewed": false,
  "format": "onnx",
  "expected_runtime": "onnxruntime-cpu/directml",
  "blocked_reason": "Non-commercial license; unavailable in commercial mode."
}
```

If MMS_FA is used in a project, store non-commercial contamination metadata immediately.

## Experimental model lane

Experimental aligners are allowed only behind manifests. Do not add a vague “auto aligner” provider without specifying:

- sample rate,
- model format,
- token inventory,
- transcript normalization rules,
- language scope,
- confidence semantics,
- commercial/non-commercial status,
- runtime requirements,
- output contract.

Experimental aligners may be used for benchmarks, but they should not become default unless the commercial and stability gates pass.

## Deliverables

Core deliverables:

- `IForcedAligner` interface.
- `ForcedAlignmentResult` data type.
- `WordTiming` data type.
- `PhonemeTiming` data type.
- `AlignmentConfidence` or equivalent structured confidence type.
- `IPhonemeInventoryMapper`.
- `IPhonemeTimingPlanner`.
- `PhonemeTimingPlan`.
- `IPhonemeStretchService`.
- `PhonemeStretchResult`.
- `LipSyncStagePlan`.
- Per-segment lip sync status model.
- Pipeline config toggle.
- Manifest gate before real alignment.
- Fake/test-double implementations.

Suggested interface sketch:

```csharp
public interface IForcedAligner
{
    Task<ForcedAlignmentResult> AlignAsync(
        ForcedAlignmentRequest request,
        CancellationToken cancellationToken);
}

public sealed record ForcedAlignmentRequest(
    string AudioPath,
    string TranscriptText,
    string? LanguageCode,
    string SegmentId,
    ForcedAlignmentOptions Options);

public sealed record ForcedAlignmentResult(
    string SegmentId,
    ForcedAlignmentStatus Status,
    IReadOnlyList<WordTiming> Words,
    IReadOnlyList<PhonemeTiming> Phonemes,
    AlignmentConfidence Confidence,
    string? SkipReason,
    string? ProviderId,
    string? ModelId);
```

Suggested timing records:

```csharp
public sealed record WordTiming(
    string Text,
    TimeSpan Start,
    TimeSpan End,
    double Confidence);

public sealed record PhonemeTiming(
    string Symbol,
    string Inventory,
    TimeSpan Start,
    TimeSpan End,
    double Confidence,
    string? WordText = null);
```

Suggested stage statuses:

```csharp
public enum LipSyncSegmentStatus
{
    NotRun,
    Aligned,
    Partial,
    SkippedLowConfidence,
    SkippedNoPhonemes,
    SkippedInventoryMismatch,
    SkippedUnsafeStretchRatio,
    SkippedLicenseGate,
    SkippedRuntimeUnavailable,
    Failed
}
```

## Phoneme timing plan

`PhonemeTimingPlan` maps source phoneme/viseme intervals to the TTS audio intervals that should be adjusted.

Suggested shape:

```csharp
public sealed record PhonemeTimingPlan(
    string SegmentId,
    string SourceAlignmentId,
    string TtsAlignmentId,
    IReadOnlyList<PhonemeTimingAdjustment> Adjustments,
    TimeSpan TargetDuration,
    double PlanConfidence,
    string? DegradationReason);

public sealed record PhonemeTimingAdjustment(
    string SourceSymbol,
    string TargetSymbol,
    string InternalVisemeClass,
    TimeSpan SourceStart,
    TimeSpan SourceEnd,
    TimeSpan TtsStart,
    TimeSpan TtsEnd,
    TimeSpan DesiredTtsStart,
    TimeSpan DesiredTtsEnd,
    double StretchRatio,
    PhonemeAdjustmentKind Kind);
```

The plan must survive imperfect mapping. It should explicitly represent partial alignment instead of pretending every source phone has a perfect target counterpart.

## Phoneme inventory mapping

English phoneme symbols can come from IPA, ARPABET, eSpeak, model-specific tokens, or TTS provider-specific phonemes. V22 must normalize them into an internal inventory.

Add `IPhonemeInventoryMapper` to isolate this swamp before it leaks into timing code.

Requirements:

- Normalize source aligner symbols.
- Normalize TTS phoneme symbols.
- Map to internal phoneme or viseme classes.
- Track unmapped symbols.
- Degrade to word-level timing or skip if mapping confidence is too low.
- Record inventory mismatch as a structured status.

Do not let string equality between raw phoneme symbols define alignment quality.

## Phoneme stretch service

`IPhonemeStretchService` applies conservative timing correction on top of the existing audio time-stretch infrastructure.

This service should not naïvely split and stretch every phoneme. That will produce brittle artifacts. A technically duration-correct file can still sound terrible.

Hard constraints:

- Preserve pitch.
- Preserve target segment duration within 30 ms.
- Clamp stretch ratios.
- Do not independently stretch tiny consonants.
- Merge very short regions into neighbors.
- Prefer vowels/nuclei for duration correction.
- Add crossfades at edit boundaries.
- Preserve original atempo take if correction is unsafe.
- Return a structured skipped result when unsafe.

Suggested default safety values for initial implementation:

| Constraint | Initial value | Notes |
|---|---:|---|
| Minimum independent region | 40 ms | Shorter regions merge or remain unchanged |
| Consonant ratio range | 0.75x-1.35x | Tighter to avoid obvious artifacts |
| Vowel ratio range | 0.65x-1.50x | Vowels tolerate more correction |
| Segment duration tolerance | 30 ms | Acceptance criterion |
| Edit-boundary crossfade | 5-15 ms | Tune by test clip |
| Maximum unmapped phone fraction | 25% | Above this, partial/skip |

These values can be adjusted by tests and benchmark clips, but the first implementation should be conservative.

## Runtime/hardware requirements

V22 should be realistic on CPU and optionally accelerated through ONNX Runtime providers such as DirectML or future Windows ML paths where practical.

Runtime requirements:

- FFmpeg available for audio extraction/manipulation.
- ONNX Runtime for real commercial/non-commercial aligners.
- 16 kHz mono audio normalization for wav2vec2-style models.
- No Python required in the end-user runtime for the stable commercial V22 path.

Hardware posture:

- CPU should be acceptable for alignment on short segments.
- DirectML/Windows ML may improve throughput but must not be required for basic V22 functionality.
- Hardware provider availability must be detected, not guessed.

## License/manifest gates

Before real alignment runs:

- manifest exists,
- checksum verified,
- code and weight license metadata present,
- commercial mode allows the selected provider,
- non-commercial contamination recorded if applicable,
- runtime available,
- model files installed,
- sample rate/input contract satisfied.

If a gate fails, preserve the post-atempo take and mark the segment/stage with `SkippedLicenseGate`, `SkippedRuntimeUnavailable`, or another exact reason.

## UI changes

Add per-segment lip sync status in the segment list or details panel:

- Aligned.
- Partial.
- Skipped.
- Failed.

UI should expose the specific reason on hover/details:

- low confidence,
- no phonemes,
- inventory mismatch,
- unsafe stretch ratio,
- license gate,
- runtime unavailable,
- failure.

Add pipeline configuration:

- Enable phoneme-aligned lip sync.
- Provider selection if multiple providers are available.
- Commercial/non-commercial warning when relevant.
- Experimental label when relevant.

Do not let the presence of a toggle imply model readiness.

## Fakes and test doubles

Add to `tests/Trackdub.TestDoubles/`:

- `FakeForcedAligner`
  - Reads deterministic fixture JSON or accepts in-memory fixture data.
  - No audio I/O.
  - Configurable confidence.
  - Configurable empty/no-phoneme result.
  - Configurable failure.

- `FakePhonemeStretchService`
  - Records the last `PhonemeTimingPlan`.
  - Records call count and inputs.
  - Returns input WAV unchanged by default.
  - Can return skipped/failure statuses.

Fixture example:

```json
{
  "segment_id": "seg-001",
  "confidence": 0.92,
  "words": [
    { "text": "cat", "start_ms": 120, "end_ms": 430, "confidence": 0.95 }
  ],
  "phonemes": [
    { "symbol": "k", "inventory": "ipa", "start_ms": 120, "end_ms": 170, "confidence": 0.94 },
    { "symbol": "æ", "inventory": "ipa", "start_ms": 170, "end_ms": 360, "confidence": 0.93 },
    { "symbol": "t", "inventory": "ipa", "start_ms": 360, "end_ms": 430, "confidence": 0.91 }
  ]
}
```

## Acceptance criteria

V22 is accepted when:

- The fake-backed stage is integrated and registered after TTS timing reconciliation.
- `IForcedAligner` returns word and phoneme timing data for an English fixture segment.
- `PhonemeTimingPlan` represents source-to-TTS timing adjustments.
- `IPhonemeStretchService` can produce a WAV matching target duration within 30 ms on a controlled test case.
- Low-confidence segments skip cleanly and preserve the original post-atempo take.
- The pipeline toggle disables the stage without calling aligner/stretch services.
- Per-segment UI status reflects aligned, partial, skipped, and failed states.
- Manifest gates block providers whose model is missing, unreviewed, or not commercial-safe in commercial mode.
- Non-commercial providers mark project contamination and are blocked from commercial mode.

## Non-goals

- No video frame manipulation.
- No mouth overlay/timeline preview.
- No full multilingual alignment guarantee.
- No portrait animation.
- No real-time alignment overlay.
- No non-commercial model in commercial mode.

## Risks

- ONNX export quality for phoneme CTC models may vary.
- Forced alignment quality degrades on fast speech, noise, overlapping speakers, and transcript mismatch.
- Phoneme inventory mapping can create fake precision if raw symbol sets are treated as equivalent.
- Per-phoneme stretching can introduce audible artifacts.
- Duration matching alone can hide bad audio quality.

## Tests

Fast tests:

- `FakeForcedAligner` returns fixture timestamps.
- `FakeForcedAligner` can return low confidence.
- `FakeForcedAligner` can return no phonemes.
- Stage disabled means aligner and stretcher are never called.
- Low-confidence segment preserves original atempo take.
- Missing manifest blocks real provider.
- Non-commercial provider blocked in commercial mode.
- Inventory mismatch returns partial or skipped status.
- Unsafe stretch ratio preserves original take.
- Stretch service duration within 30 ms on synthetic fixture.
- Artifact metadata records provider/model/stage.

Integration/benchmark tests, separate from fast tests:

- Real ONNX aligner smoke test on short English audio.
- FFmpeg stretch smoke test.
- Alignment throughput benchmark.
- Audio artifact audit on representative samples.

## Agent implementation prompt

```text
Implement V22 fake-backed architecture before real model wiring.

Read AGENT_CONTEXT.md first. Search the repository for existing pipeline stage, artifact, manifest, provider, test-double, and UI status patterns before adding new abstractions.

Add IForcedAligner, ForcedAlignmentResult, WordTiming, PhonemeTiming, AlignmentConfidence, IPhonemeInventoryMapper, IPhonemeTimingPlanner, PhonemeTimingPlan, IPhonemeStretchService, and LipSyncStagePlan.

Add FakeForcedAligner in tests/Trackdub.TestDoubles using fixture JSON or deterministic in-memory fixtures. Add FakePhonemeStretchService that records the stretch plan and returns the input WAV unchanged.

Register LipSyncStagePlan after TTS timing reconciliation. Add a pipeline toggle and per-segment lip sync status. Implement confidence-threshold skip, no-phoneme skip, inventory-mismatch skip, unsafe-stretch skip, and manifest-license gate.

Do not wire real ONNX alignment until the fake-backed stage, tests, manifest gate, and statuses are working. Do not implement video manipulation.
```

---

# V23 — Video lip synthesis, original-footage repair

## Goal

Repair mouth motion in the original video so it better matches the dubbed audio. V23 preserves the original footage and modifies only detected face/mouth regions when quality gates pass.

V23 is not full portrait generation. It is the “make the original dub look less dubbed” milestone.

## Position in pipeline

V23 runs after V22 phoneme-aligned audio, though it may also run on normal post-atempo dubbed audio if V22 was disabled or skipped.

Suggested ordering:

```text
TTS timing reconciliation
→ V22 phoneme-aligned audio, optional
→ V23 speaker-turn lip synthesis
→ export / preview artifact
```

## Projects touched

Expected projects:

- `src/Trackdub.Inference/`
- `src/Trackdub.Inference.Onnx/`
- `src/Trackdub.Application/`
- `src/Trackdub.App.Avalonia/`
- `tests/Trackdub.TestDoubles/`
- relevant application/unit/integration test projects

If a Python/CUDA provider is added later, keep it outside the stable end-user runtime path until packaging rules are explicitly defined.

## Commercial model lane

Commercial candidate:

- MuseTalk 1.5.
- Expected posture: best current open commercial-candidate for audio-driven mouth/lip synchronization.
- Claimed fit: modifies an existing face region according to audio.
- Runtime posture: Python/CUDA first.
- Stability posture: experimental runtime, not stable/default.
- ONNX posture: deferred until export/operator/runtime compatibility is proven.

Important distinction:

MuseTalk can be commercial-candidate and experimental at the same time. Commercial-candidate means the license path may be viable after human review. Experimental means the runtime, packaging, GPU requirements, quality, and integration path are not stable enough for the default product lane.

Recommended provider names:

- `PythonMuseTalkLipSynthesisEngine`
- `MuseTalkLipSynthesisProvider`

Do not make `OnnxLipSynthesisEngine` the primary real V23 deliverable. ONNX for video synthesis is a future proof-of-export prize, not the entrance ticket.

Commercial manifest shape:

```json
{
  "model_id": "musetalk-v1-5",
  "provider_id": "python-musetalk-lip-synthesis",
  "task": "lip_synthesis",
  "source_url": "https://github.com/TMElyralab/MuseTalk",
  "code_license": "MIT",
  "weight_license": "commercial-use-statement-present-pending-review",
  "dependency_licenses": [
    { "name": "sd-vae-ft-mse", "license": "pending-review", "commercial_allowed": null },
    { "name": "whisper", "license": "pending-review", "commercial_allowed": null },
    { "name": "dwpose", "license": "pending-review", "commercial_allowed": null },
    { "name": "face-parsing", "license": "pending-review", "commercial_allowed": null },
    { "name": "face-alignment", "license": "pending-review", "commercial_allowed": null },
    { "name": "s3fd", "license": "pending-review", "commercial_allowed": null }
  ],
  "commercial_allowed": false,
  "noncommercial_allowed": true,
  "experimental": true,
  "human_reviewed": false,
  "format": "external-python-provider",
  "expected_runtime": "python-cuda",
  "supported_hardware": ["cuda"],
  "minimum_vram_mb": 8192,
  "recommended_vram_mb": 12288,
  "quality_caveats": [
    "Runtime packaging not stable for default product lane.",
    "Full pipeline throughput must be benchmarked, not inferred from model-only fps claims.",
    "Dependency licenses require human review."
  ],
  "blocked_reason": "Dependency license review incomplete."
}
```

Once dependency licenses and weight terms are reviewed, `commercial_allowed` may become true. Until then, commercial mode should block it.

## Non-commercial model lane

Non-commercial/research candidates:

- Wav2Lip baseline.
- LatentSync.
- EchoMimic/EchoMimicV2.
- Hallo2.

Rules:

- Wav2Lip is useful as a historical baseline, but public pretrained model/results are not commercial-clean by default.
- LatentSync is promising, but the weight/license stack is not clean enough for commercial default without legal review.
- EchoMimic and Hallo2 are more portrait/human-animation oriented and should not be V23 commercial defaults.
- All of these must be blocked in commercial mode unless a later legal pass proves otherwise.

Suggested non-commercial provider behavior:

- Show research/non-commercial warning.
- Mark project contamination immediately.
- Preserve model/provider metadata in generated artifacts.
- Do not allow commercial export mode if used.

## Experimental model lane

The first real V23 provider should be experimental even if it is commercially promising.

Experimental V23 providers may require:

- Python runtime,
- CUDA GPU,
- external model files,
- provider-specific preprocessing,
- temporary frame directories,
- FFmpeg frame extraction/recomposition,
- GPU memory checks,
- full-pipeline benchmarks.

Experimental providers must never block stable audio-only export. If V23 cannot run, preserve the previous usable video/audio artifacts and mark exact skip reasons.

## Deliverables

Core deliverables:

- `ILipSynthesisEngine`.
- `LipSynthesisRequest`.
- `LipSynthesisResult`.
- `LipSynthesisStagePlan`.
- `IFaceDetector`.
- `IFaceLandmarkProvider`.
- `IFacePoseEstimator`.
- `FaceDetectionResult`.
- `FaceLandmarkResult`.
- `FacePoseEstimate`.
- `SpeakerTurnCropPlan`.
- `VideoRecompositionPlan`.
- Per-segment/turn synthesis status.
- Pipeline synthesis toggle.
- Warning banner when any turns are skipped.
- Manifest gates for detector, landmark, pose, and synthesis providers.
- Fake/test-double providers.

Suggested interface sketch:

```csharp
public interface ILipSynthesisEngine
{
    Task<LipSynthesisResult> SynthesizeAsync(
        LipSynthesisRequest request,
        CancellationToken cancellationToken);
}

public sealed record LipSynthesisRequest(
    string OriginalVideoPath,
    string DubbedAudioPath,
    IReadOnlyList<SpeakerTurnSynthesisRequest> SpeakerTurns,
    LipSynthesisOptions Options);

public sealed record SpeakerTurnSynthesisRequest(
    string SegmentId,
    TimeSpan Start,
    TimeSpan End,
    string? SpeakerId,
    string? PhonemeTimingPlanId,
    string? FaceTrackId);
```

Do not force `PhonemeTimingPlan` as a required input. Many lip synthesis models condition on audio features rather than symbolic phonemes. V22 improves the audio; V23 mostly needs the improved audio.

Suggested statuses:

```csharp
public enum LipSynthesisSegmentStatus
{
    NotRun,
    Synthesized,
    SkippedNoFace,
    SkippedNonFrontal,
    SkippedLowConfidence,
    SkippedOccluded,
    SkippedUnstableCrop,
    SkippedLicenseGate,
    SkippedRuntimeUnavailable,
    Failed
}
```

## Speaker-turn processing rule

V23 must process by speaker turn, not whole video.

Speaker-turn processing benefits:

- Limits GPU memory pressure.
- Improves resumability.
- Makes skip reasons segment-specific.
- Preserves original frames for skipped turns.
- Avoids unnecessary synthesis on silence/non-speaker frames.
- Fits existing segment-oriented UI/status model.

For skipped turns, original frames must be preserved in the recomposed artifact.

## Face quality guards

V23 should skip rather than hallucinate when face evidence is poor.

Initial quality gates:

| Gate | Initial rule | Status |
|---|---|---|
| No primary face | no usable detection | `SkippedNoFace` |
| Detection confidence | below threshold | `SkippedLowConfidence` |
| Pose | yaw or pitch > 30 degrees | `SkippedNonFrontal` |
| Landmarks | missing/unstable | `SkippedUnstableCrop` |
| Occlusion | mouth/face occluded | `SkippedOccluded` |
| Runtime | provider unavailable | `SkippedRuntimeUnavailable` |
| License | manifest blocked | `SkippedLicenseGate` |

The exact numeric thresholds should be configurable and tested. The milestone should start conservative.

## Runtime/hardware requirements

Stable architecture path:

- FFmpeg for frame extraction and recomposition.
- ONNX may be realistic for face detection and landmarks.
- Python/CUDA acceptable for experimental real synthesis provider.

Video synthesis hardware posture:

- CUDA-capable NVIDIA GPU should be assumed for real MuseTalk-class provider.
- Treat 12 GB VRAM as the practical target for a good initial experience until Trackdub has its own benchmarks.
- 8 GB GPUs may run only in limited mode with warnings.
- CPU fallback is not production-usable for real video synthesis and must show a strong warning if exposed.
- Do not infer full export speed from model-only FPS claims.

Important performance interpretation:

A claim like “30fps+ on Tesla V100” should be treated as a model-inference benchmark for a constrained face/mouth region. It does not mean full end-to-end 1080p/4K export, including detection, tracking, compositing, encoding, artifact bookkeeping, and UI progress, will run at realtime.

## License/manifest gates

V23 has multiple model slots:

- face detection,
- landmarking,
- pose estimation,
- lip synthesis,
- optional audio feature extraction provider,
- optional face parsing/masking provider.

All must be manifest-gated. The stage must not run if any required provider is missing license metadata, checksum, runtime metadata, or commercial-mode approval.

V23 should support this result:

```text
stage blocked because synthesis provider dependency license review incomplete
```

instead of pretending partial model availability equals readiness.

## UI changes

Add synthesis UI elements:

- Pipeline toggle: “Video lip synthesis.”
- Provider selector if multiple providers exist.
- Experimental label for Python/CUDA providers.
- Commercial/non-commercial warning.
- Segment/turn synthesis status column.
- Warning banner when any turns are skipped.
- Runtime readiness details.
- GPU/memory warning.
- Details view for skip/failure reason.

User-facing status examples:

- “Synthesized.”
- “Skipped: no face detected.”
- “Skipped: face angle too large.”
- “Skipped: model license not approved for commercial mode.”
- “Skipped: CUDA runtime unavailable.”
- “Failed: recomposition error.”

Do not show “lip sync complete” when half the segments were skipped. Show partial completion.

## Fakes and test doubles

Add to `tests/Trackdub.TestDoubles/`:

- `FakeLipSynthesisEngine`
  - Returns original video path unchanged by default.
  - Records call count and inputs.
  - Configurable per-segment success/skip/failure.

- `FakeFaceDetector`
  - Returns configurable bounding boxes.
  - Returns configurable confidence scores.
  - No image I/O.

- `FakeFaceLandmarkProvider`
  - Returns deterministic landmark fixtures.
  - Can simulate missing/unstable landmarks.

- `FakeFacePoseEstimator`
  - Returns deterministic yaw/pitch/roll.
  - Can simulate non-frontal faces.

## Acceptance criteria

V23 is accepted when:

- Fake-backed `LipSynthesisStagePlan` is registered after V22.
- Stage processes speaker turns, not the entire video as one opaque unit.
- Stage toggle prevents `ILipSynthesisEngine` from being called when disabled.
- No-face, non-frontal, low-confidence, occluded, unstable-crop, license-gate, and runtime-unavailable paths skip cleanly.
- Skipped turns preserve original frames/artifacts.
- Per-segment synthesis status reflects exact outcome.
- UI shows synthesis status and warning banner for skipped turns.
- Manifest gate blocks missing, unreviewed, non-commercial, or runtime-unavailable providers.
- Experimental provider state is visible and does not imply stable readiness.

A real MuseTalk provider is not required for the first V23 architecture pass. If added later, it must be behind experimental runtime checks and manifests.

## Non-goals

- No full talking-head generation.
- No portrait/avatar generation.
- No torso/body animation.
- No real-time lip synthesis.
- No multi-face synthesis beyond the primary speaker turn.
- No default use of Wav2Lip/LatentSync/EchoMimic/Hallo2 in commercial mode.
- No ONNX synthesis claim without proof.

## Risks

- License risk remains the primary blocker for real synthesis providers.
- Dependency models may have licenses that differ from the main repo.
- Runtime packaging for Python/CUDA can be hostile to low-friction desktop UX.
- Face crops can jitter frame-to-frame.
- Seam artifacts can make synthesis visibly worse than the original.
- GPU memory pressure may be high on long clips.
- CPU fallback may be unusably slow.

## Tests

Fast tests:

- Stage disabled means engine is never called.
- Fake engine called once per eligible speaker turn.
- No-face skip preserves original artifact.
- Non-frontal skip preserves original artifact.
- Low-confidence skip preserves original artifact.
- Occlusion skip preserves original artifact.
- Runtime unavailable blocks stage.
- License gate blocks stage.
- Per-segment status updates correctly.
- Warning banner state appears when any segment skipped.
- Artifact metadata records provider/model/stage/experimental lane.

Integration/benchmark tests, separate from fast tests:

- FFmpeg frame extraction/recomposition smoke test.
- Face detection ONNX smoke test, if a commercial-safe detector is approved.
- End-to-end synthesis benchmark for experimental provider.
- GPU memory and throughput benchmark.
- Visual artifact inspection clips.

## Agent implementation prompt

```text
Implement V23 fake-backed architecture only unless explicitly instructed to add a real experimental provider.

Read AGENT_CONTEXT.md first. Search the repository for existing pipeline stage, artifact, manifest, provider, and UI status patterns before coding.

Add ILipSynthesisEngine, IFaceDetector, IFaceLandmarkProvider, IFacePoseEstimator, LipSynthesisStagePlan, FaceDetectionResult, FaceLandmarkResult, FacePoseEstimate, SpeakerTurnCropPlan, and per-segment LipSynthesisSegmentStatus.

Add FakeLipSynthesisEngine, FakeFaceDetector, FakeFaceLandmarkProvider, and FakeFacePoseEstimator in tests/Trackdub.TestDoubles. Fakes must avoid real video/image/model I/O and expose call counts and configurable skip reasons.

Register LipSynthesisStagePlan after V22. Process speaker turns, not whole videos. Add quality guards for no face, low confidence, non-frontal pose, occlusion, unstable crop, license gate, and runtime unavailable. Preserve original artifacts on all skips.

Add UI status projection and warning banner. Do not wire Wav2Lip, LatentSync, MuseTalk, EchoMimic, or Hallo2 as real providers unless manifests and dependency licenses are reviewed. Do not claim ONNX support for video synthesis.
```

---

# V24 — Portrait animation provider API

## Goal

Introduce portrait animation as a separate generated-performance branch. This mode creates a new talking-head or portrait video from an identity source and dubbed audio.

V24 is not original-footage repair. It is generated performance. It should not be hidden behind `ILipSynthesisEngine`.

## Product intent

Portrait animation lets Trackdub support workflows such as:

- generated multilingual presenters,
- avatar-based educational localization,
- narrated article/video transformations,
- creator avatar dubbing,
- synthetic host workflows,
- accessibility avatars,
- non-commercial research demos.

This mode should be explicit and labeled. The user should understand that Trackdub is generating a new portrait video, not repairing the original clip.

## Position in pipeline

V24 branches after dubbed audio is available. It may consume V22 timing metadata, but the primary input is dubbed audio plus identity source.

Suggested conceptual flow:

```text
source media / transcript / translation / TTS
→ dubbed audio
→ optional V22 timing metadata
→ V24 portrait animation
→ generated portrait video artifact
```

V24 should not require V23 to run.

## Projects touched

Expected projects:

- `src/Trackdub.Inference/`
- `src/Trackdub.Application/`
- `src/Trackdub.App.Avalonia/`
- `tests/Trackdub.TestDoubles/`
- possibly `src/Trackdub.Inference.Onnx/` for future ONNX-compatible portrait providers

## Commercial model lane

Commercial candidate:

- MuseTalk-derived portrait workflows, only if dependency/license review passes.

Possible input shapes:

- single identity image,
- short identity clip,
- selected face crop from source media,
- dubbed audio,
- optional speaker/style metadata,
- optional performance graph.

Commercial posture:

- Do not expose as stable/default until provider runtime and dependency licenses are reviewed.
- If MuseTalk remains Python/CUDA-first, label it experimental even if commercial allowed.
- Do not bundle public-figure identities or celebrity presets.

Commercial mode should support user-provided identity sources only when provenance and consent fields are captured.

## Non-commercial model lane

Non-commercial/research portrait candidates:

- SadTalker-style talking-head animation.
- Hallo2.
- EchoMimic/EchoMimicV2.
- LatentSync variants when used as portrait generation rather than original-footage repair.
- Wav2Lip only as a baseline/comparison provider.

Rules:

- These providers must be opt-in.
- They must mark project contamination when non-commercial.
- They must not be used in commercial-mode export unless a separate legal review approves them.
- They should not be conflated with V23 original-video repair.

## Experimental model lane

Most real V24 providers should start experimental. The stable V24 deliverable is the API, project model, UI separation, fake provider, and manifest gate.

Experimental provider requirements:

- explicit runtime checks,
- hardware checks,
- model manifest,
- identity-source validation,
- generated-artifact metadata,
- clear output labeling,
- no fake readiness.

## Deliverables

Core deliverables:

- `IPortraitAnimationEngine`.
- `PortraitAnimationStagePlan`.
- `PortraitAnimationRequest`.
- `PortraitAnimationResult`.
- `PortraitIdentitySource`.
- `PortraitAnimationOptions`.
- Generated portrait video artifact type.
- Portrait mode toggle separate from V23.
- Provider manifest support for portrait providers.
- Project contamination metadata when non-commercial providers are used.
- Fake provider and tests.

Suggested interface sketch:

```csharp
public interface IPortraitAnimationEngine
{
    Task<PortraitAnimationResult> AnimateAsync(
        PortraitAnimationRequest request,
        CancellationToken cancellationToken);
}

public sealed record PortraitAnimationRequest(
    string DubbedAudioPath,
    PortraitIdentitySource IdentitySource,
    IReadOnlyList<PortraitSpeakerTurn> SpeakerTurns,
    PortraitAnimationOptions Options);

public sealed record PortraitIdentitySource(
    PortraitIdentitySourceKind Kind,
    string SourcePath,
    string? SpeakerId,
    IdentityProvenance Provenance);

public enum PortraitIdentitySourceKind
{
    StillImage,
    ShortClip,
    SourceVideoFaceCrop,
    AvatarIdentityPack
}
```

Suggested status enum:

```csharp
public enum PortraitAnimationStatus
{
    NotRun,
    Generated,
    Partial,
    SkippedNoIdentitySource,
    SkippedInvalidIdentitySource,
    SkippedLicenseGate,
    SkippedRuntimeUnavailable,
    SkippedHardwareInsufficient,
    Failed
}
```

## Performance graph concept

V24 should introduce the idea of a provider-neutral performance graph, but it does not need to fully implement it.

A future `IPerformanceGraphBuilder` can transform audio/timing/speaker metadata into structured performance signals:

- phonemes,
- visemes,
- expression intensity,
- blink cadence,
- head motion,
- eye gaze,
- emotion/style tags,
- body sway,
- gesture timing.

Different renderers can consume different parts:

| Renderer | Consumes |
|---|---|
| V23 lip repair | audio, optional phoneme/viseme timing, face tracks |
| V24 portrait animation | audio, identity source, optional performance graph |
| V26 realtime preview | chunked audio, cached identity state, low-latency graph |

Do not overbuild the graph in V24. Add only enough seams to avoid boxing future work into a mouth-only abstraction.

## Runtime/hardware requirements

V24 real providers are likely GPU-heavy.

Initial stable path:

- fake provider only,
- manifest gates,
- project model,
- UI separation,
- generated artifact plumbing.

Experimental real providers:

- likely Python/CUDA,
- likely 12 GB+ VRAM target for good experience,
- CPU fallback not production-usable,
- ONNX/DirectML not promised.

## License/manifest gates

V24 identity generation providers must be gated by:

- model license,
- dependency licenses,
- runtime availability,
- commercial/non-commercial mode,
- identity provenance,
- consent metadata where applicable.

A model provider being license-clean does not make a specific identity source safe. User-provided identity assets need separate provenance/consent metadata.

## UI changes

Add a portrait mode panel or branch distinct from lip synthesis:

- “Generated portrait” terminology.
- Identity source selector.
- Provider selector.
- Commercial/non-commercial warning.
- Experimental runtime label.
- Generated portrait artifact preview.
- Stage status.
- Skip/failure reason details.

Avoid ambiguous UI labels such as “fix lips” for portrait animation. V24 is not a fix; it is a generated output.

## Fakes and test doubles

Add to `tests/Trackdub.TestDoubles/`:

- `FakePortraitAnimationEngine`
  - Returns a deterministic generated artifact path.
  - Records request and call count.
  - Configurable success/partial/skip/failure.

- Optional `FakePerformanceGraphBuilder`
  - Returns deterministic graph fixture.
  - No audio/model I/O.

## Acceptance criteria

V24 is accepted when:

- `IPortraitAnimationEngine` exists separately from `ILipSynthesisEngine`.
- Fake-backed `PortraitAnimationStagePlan` can generate a portrait artifact record.
- Portrait mode has a separate toggle from video lip synthesis.
- UI labels output as generated portrait, not repaired original video.
- Missing identity source skips cleanly.
- Invalid identity source skips cleanly.
- Manifest/license gate blocks unavailable providers.
- Non-commercial providers mark project contamination.
- Experimental providers show experimental status.
- Commercial mode blocks non-commercial providers.

## Non-goals

- No reusable identity library yet. That belongs to V25.
- No real-time preview. That belongs to V26.
- No full-body animation as the default milestone target.
- No cloud avatar service.
- No bundled public-figure avatar library.
- No automatic consent inference.
- No real provider unless explicitly approved by manifest/license/runtime review.

## Risks

- Strong portrait models are often license-sensitive.
- Portrait generation can produce identity drift.
- Users may confuse generated portrait output with repaired original footage.
- Python/CUDA packaging can complicate desktop distribution.
- Research models may require large VRAM budgets.
- Poor identity provenance can create product/legal risk.

## Tests

Fast tests:

- Stage disabled means provider is never called.
- Missing identity source returns `SkippedNoIdentitySource`.
- Invalid identity source returns `SkippedInvalidIdentitySource`.
- Missing manifest blocks provider.
- Non-commercial provider blocked in commercial mode.
- Non-commercial project contamination stored when used.
- Fake provider records request.
- Generated artifact metadata includes provider/model/stage/lane.
- UI status projection distinguishes generated/partial/skipped/failed.

Integration/benchmark tests, separate from fast tests:

- Real provider smoke test only after license/runtime review.
- Identity-source preprocessing benchmark.
- Generated portrait artifact encode smoke test.

## Agent implementation prompt

```text
Implement V24 portrait-animation architecture as a separate generated-performance branch.

Read AGENT_CONTEXT.md first. Search for existing artifact, stage, provider, manifest, and UI status patterns. Do not reuse ILipSynthesisEngine for portrait animation.

Add IPortraitAnimationEngine, PortraitAnimationStagePlan, PortraitAnimationRequest, PortraitAnimationResult, PortraitIdentitySource, PortraitAnimationOptions, PortraitAnimationStatus, and generated portrait artifact metadata.

Add FakePortraitAnimationEngine in tests/Trackdub.TestDoubles. It must avoid real media/model I/O, record inputs, and support success/skip/failure.

Add portrait mode toggle separate from V23 video lip synthesis. Add UI wording that clearly says generated portrait. Implement missing identity source skip, invalid identity source skip, manifest gate, commercial/non-commercial gate, experimental label, and project contamination metadata.

Do not wire real portrait models unless explicitly requested and license/runtime manifests are reviewed.
```

---

# V25 — Avatar identity packs and reusable speaker identities

## Goal

Let users create, store, assign, and reuse avatar/identity packs across projects while keeping consent, provenance, license state, model dependencies, and commercial/non-commercial contamination explicit.

V25 turns portrait animation from one-off source selection into a governed identity workflow.

## Product intent

Users should be able to create identity packs for recurring speakers or avatars, then assign those packs to speakers in future projects.

Examples:

- A creator stores their own presenter avatar.
- A company stores an approved training narrator identity.
- A user assigns different generated presenters to translated speakers.
- A non-commercial researcher stores model-specific identity embeddings with clear contamination labels.

Identity packs must not become a stealth celebrity/avatar library. No bundled public-figure identities.

## Position in pipeline

V25 is not just a stage. It is project/domain infrastructure that feeds V24 and V26.

Conceptual flow:

```text
identity source
→ identity validation/provenance
→ avatar identity pack
→ speaker-to-avatar assignment
→ portrait animation / realtime preview
```

## Projects touched

Expected projects:

- `src/Trackdub.Domain/` for pure identity/provenance value types if appropriate.
- `src/Trackdub.Application/` for identity services, assignment, project state.
- `src/Trackdub.Infrastructure/` for persistence if identity packs are stored in project/global databases.
- `src/Trackdub.App.Avalonia/` for UI.
- `src/Trackdub.Inference/` for provider contracts.
- `tests/Trackdub.TestDoubles/`.

Respect existing dependency direction. Domain must not depend on infrastructure, inference, UI, or application.

## Commercial model lane

Commercial mode supports:

- user-provided identity images,
- user-provided short identity clips,
- selected face crops from user-owned source media,
- locally stored identity packs,
- speaker-to-avatar assignment,
- explicit consent/provenance metadata,
- provider-specific generated caches if provider is commercial-safe.

Commercial mode must not include:

- bundled celebrity identities,
- public-figure avatar packs,
- automatic consent inference,
- non-commercial provider embeddings,
- identity packs created by blocked providers.

Commercial identity packs should be allowed only when:

- identity source is user supplied or otherwise documented,
- consent state is recorded,
- provider dependencies are commercial-safe,
- pack metadata is complete enough to explain provenance.

## Non-commercial model lane

Non-commercial mode may support:

- research provider identity embeddings,
- identity packs generated with non-commercial models,
- non-commercial avatar preparation steps.

But it must:

- mark the identity pack as non-commercial contaminated,
- mark any project using it as non-commercial contaminated,
- block commercial export/workflow modes,
- preserve provider/model metadata.

## Experimental model lane

Experimental identity providers may create:

- embeddings,
- face descriptors,
- provider-specific cached crops,
- latent identity representations,
- prepared portrait model state,
- thumbnail/contact sheet previews.

These artifacts must be versioned because provider internals can change.

Experimental identity cache requirements:

- provider id,
- provider version,
- model id,
- model checksum,
- source identity checksum,
- created timestamp,
- compatibility metadata,
- lane metadata,
- invalidation rules.

## Deliverables

Core deliverables:

- `AvatarIdentityPack`.
- `AvatarIdentityPackId` or equivalent.
- `IdentityProvenance`.
- `IdentityConsentState`.
- `IdentityLicenseState`.
- `AvatarIdentityDependency`.
- `IAvatarIdentityProvider`.
- Identity pack import/export.
- Speaker-to-avatar assignment.
- Identity pack storage.
- Safe deletion and cache invalidation.
- Missing-provenance warnings.
- Fake identity provider and tests.

Suggested data shape:

```csharp
public sealed record AvatarIdentityPack(
    string Id,
    string DisplayName,
    IdentityProvenance Provenance,
    IdentityConsentState ConsentState,
    IdentityLicenseState LicenseState,
    IReadOnlyList<AvatarIdentityDependency> Dependencies,
    IReadOnlyList<AvatarIdentityArtifact> Artifacts,
    bool CommercialAllowed,
    bool NonCommercialContaminated,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record IdentityProvenance(
    IdentitySourceType SourceType,
    string? SourcePath,
    string? SourceChecksum,
    bool UserSupplied,
    string? Notes);

public enum IdentityConsentState
{
    Unknown,
    UserConfirmed,
    DocumentedExternalPermission,
    NotRequiredForSyntheticOnly,
    Blocked
}
```

Suggested provider interface:

```csharp
public interface IAvatarIdentityProvider
{
    Task<AvatarIdentityPackResult> CreateOrUpdateAsync(
        AvatarIdentityPackRequest request,
        CancellationToken cancellationToken);
}
```

## Identity provenance fields

At minimum, store:

- source type,
- source path or artifact id,
- source checksum,
- user supplied flag,
- consent confirmed flag/state,
- license notes,
- provider id,
- provider version,
- model dependencies,
- created date,
- updated date,
- generated cache version,
- commercial allowed flag,
- non-commercial contamination state,
- blocked reason when applicable.

Do not infer consent. If consent is unknown, say unknown.

## Runtime/hardware requirements

V25 itself should be mostly lightweight. Identity pack management should not require GPU work unless a selected provider creates embeddings or model-specific caches.

Runtime rules:

- Import/export should work without GPU.
- Provider-specific preparation may require GPU and should be optional.
- Missing provider runtime should not delete identity packs.
- Identity caches should be invalidated or marked stale when provider/model versions change.

## License/manifest gates

Identity packs need two layers of governance:

1. Provider/model governance.
2. Identity-source provenance/consent governance.

A commercial-safe model does not make an identity source commercially safe. A user-provided image still needs provenance and consent state.

Rules:

- Packs created with non-commercial providers are non-commercial contaminated.
- Packs with unknown/blocked consent cannot be used in commercial mode unless explicitly allowed by policy.
- Packs with stale provider caches should trigger regeneration or warning.
- Packs exported/imported must preserve contamination metadata.

## UI changes

Add identity management UI:

- Identity pack list.
- Create/import identity pack.
- Identity source picker.
- Consent/provenance fields.
- License notes.
- Provider dependency status.
- Commercial-safe indicator.
- Non-commercial contamination indicator.
- Speaker-to-avatar assignment panel.
- Delete pack / remove cache actions.
- Missing provenance warnings.

UI labels should avoid legal certainty if metadata is incomplete. Prefer “Commercial metadata complete” over “legally cleared” unless the app has a real review process.

## Fakes and test doubles

Add to `tests/Trackdub.TestDoubles/`:

- `FakeAvatarIdentityProvider`
  - Creates deterministic identity packs.
  - Records request and call count.
  - Can simulate missing consent, non-commercial contamination, stale cache, and failure.

- Optional fake identity storage/repository if repository pattern exists.

## Acceptance criteria

V25 is accepted when:

- Users can create an identity pack from a local identity source.
- Identity pack stores provenance, consent, provider, dependency, and contamination metadata.
- Users can assign an identity pack to a speaker.
- Identity packs can be imported/exported while preserving metadata.
- Packs generated with non-commercial providers mark project contamination when used.
- Missing provenance/consent produces warning or block according to mode.
- Safe deletion removes caches without corrupting projects.
- Provider/model version changes can mark caches stale.
- Fake provider tests cover success, missing consent, non-commercial contamination, stale cache, and failure.

## Non-goals

- No live streaming.
- No realtime avatar rendering.
- No cloud identity service.
- No automatic consent inference.
- No bundled public-figure avatar library.
- No full-body identity packs by default.

## Risks

- Identity governance is product-sensitive and legal-sensitive.
- Users may not understand non-commercial contamination unless UI is clear.
- Provider-specific identity caches may become incompatible across model versions.
- Identity packs could accidentally leak absolute local paths if export is careless.
- Deleting caches may break reproducibility if metadata is incomplete.

## Tests

Fast tests:

- Create identity pack with complete metadata.
- Create identity pack with missing consent.
- Non-commercial provider marks identity pack contaminated.
- Using contaminated identity pack marks project contaminated.
- Commercial mode blocks contaminated identity pack.
- Import/export preserves metadata.
- Delete removes cache but preserves references safely or reports dependency.
- Provider version mismatch marks cache stale.
- Speaker-to-avatar assignment persists.
- UI projection displays commercial/non-commercial/unknown states correctly.

Integration tests:

- SQLite persistence if identity packs are stored in project database.
- File-system artifact storage for identity sources/caches.
- Import/export roundtrip.

## Agent implementation prompt

```text
Implement V25 avatar identity pack governance and reusable speaker identity assignment.

Read AGENT_CONTEXT.md first. Search the repository for project persistence, artifact, speaker assignment, provider manifest, and UI state patterns before adding new types.

Add AvatarIdentityPack, IdentityProvenance, IdentityConsentState, IdentityLicenseState, AvatarIdentityDependency, IAvatarIdentityProvider, identity pack import/export, and speaker-to-avatar assignment.

Add FakeAvatarIdentityProvider in tests/Trackdub.TestDoubles. It must create deterministic packs and support complete metadata, missing consent, non-commercial contamination, stale cache, and failure.

Store provenance, consent, provider/model dependency, commercial allowance, and non-commercial contamination metadata. Do not infer consent. Do not add bundled public-figure identities. Do not let non-commercial identity packs run in commercial mode.

Add UI for identity pack list, create/import, speaker assignment, provenance warnings, commercial-safe indicator, non-commercial contamination indicator, and safe deletion/cache invalidation.
```

---

# V26 — Low-latency portrait preview and realtime-oriented rendering

## Goal

Explore low-latency portrait generation for preview, live-ish playback, and streaming-style workflows without destabilizing offline export.

V26 is primarily experimental. It should improve feedback loops, not become a hard requirement for normal export.

## Product intent

V26 lets users preview generated portrait animation faster, test avatar/speaker assignments, and experiment with streaming-style rendering. It should not compromise the reliable offline path.

Possible future uses:

- quick preview of a translated speaker avatar,
- low-latency generated presenter playback,
- live-ish local dubbing experiments,
- provider benchmarks,
- quality/performance tuning,
- streaming-oriented demos.

## Position in pipeline

V26 extends V24/V25. It uses avatar identity packs and portrait animation providers, but it should produce preview artifacts or transient frames rather than replacing export-quality rendering.

Conceptual flow:

```text
avatar identity pack
+ chunked dubbed audio
+ optional performance graph
→ low-latency portrait renderer
→ preview frames / preview clip / benchmark output
```

Offline export remains separate:

```text
avatar identity pack
+ full dubbed audio
→ export-quality portrait renderer
→ final generated video artifact
```

## Projects touched

Expected projects:

- `src/Trackdub.Application/`
- `src/Trackdub.Inference/`
- `src/Trackdub.App.Avalonia/`
- `tests/Trackdub.TestDoubles/`
- benchmark projects
- possibly provider-specific runtime projects if added later

## Commercial model lane

Commercial mode should expose low-latency preview only for providers whose:

- commercial license is verified,
- dependency licenses are reviewed,
- runtime packaging is stable enough,
- hardware checks pass,
- performance has been measured by Trackdub benchmarks.

MuseTalk-like realtime paths may be tested here only after prior license/dependency review passes. Do not expose a normal commercial UI option based only on upstream FPS claims.

## Non-commercial model lane

Non-commercial research models may be benchmarked in non-commercial mode.

Rules:

- Mark project/session contamination when outputs are stored.
- Keep non-commercial models out of commercial workflows.
- Label preview as research/non-commercial.
- Do not allow commercial export of non-commercial preview output.

## Experimental model lane

V26 is mostly experimental.

Experimental capabilities:

- chunked audio-driven generation,
- rolling render queue,
- cached identity state,
- preview-only output mode,
- frame buffering,
- quality/performance modes,
- GPU memory budgeting,
- cancellation/resume behavior,
- local benchmark telemetry,
- degraded-mode warnings.

## Deliverables

Core deliverables:

- Realtime/low-latency provider capability flags.
- `PortraitPreviewRenderer` or equivalent application service.
- `PortraitPreviewRequest`.
- `PortraitPreviewResult`.
- Chunked audio input model.
- Rolling render queue.
- Preview-only artifact/transient-frame model.
- Benchmark harness.
- Local session performance telemetry.
- GPU readiness checks.
- Degraded-mode warnings.
- Cancellation/resume behavior.
- Clear separation between preview rendering and export rendering.

Suggested capability model:

```csharp
public sealed record PortraitProviderCapabilities(
    bool SupportsOfflineExport,
    bool SupportsLowLatencyPreview,
    bool SupportsChunkedAudio,
    bool RequiresCuda,
    bool SupportsCpuFallback,
    int MinimumVramMb,
    int RecommendedVramMb,
    TimeSpan? MinimumChunkDuration,
    TimeSpan? RecommendedChunkDuration);
```

Suggested preview status:

```csharp
public enum PortraitPreviewStatus
{
    NotRun,
    PreviewRunning,
    PreviewReady,
    Degraded,
    SkippedProviderUnsupported,
    SkippedRuntimeUnavailable,
    SkippedHardwareInsufficient,
    SkippedLicenseGate,
    Cancelled,
    Failed
}
```

## Runtime/hardware requirements

V26 must be honest about hardware.

Runtime rules:

- GPU readiness must be checked before preview starts.
- VRAM estimates must be visible when known.
- CPU fallback should be marked degraded or unavailable for heavy providers.
- Preview quality settings should be explicit.
- Export quality settings should not be silently changed by preview settings.

Hardware posture:

- CUDA likely required for real experimental portrait preview providers.
- 12 GB VRAM should be treated as practical target until project benchmarks prove otherwise.
- 8 GB VRAM should be limited/degraded if supported at all.
- CPU-only should be blocked or strongly warned for real video synthesis preview.

## License/manifest gates

Preview is not exempt from licensing.

If a preview frame or clip is generated by a non-commercial model, it must follow the same contamination rules as export artifacts when persisted.

Rules:

- Commercial mode blocks non-commercial preview providers.
- Experimental providers show experimental label.
- Missing runtime blocks preview.
- Missing benchmark data should not block experimentation, but should prevent claims like realtime-ready.

## UI changes

Add low-latency preview UI, likely inside portrait mode:

- Start/stop preview.
- Provider capability display.
- Runtime/hardware readiness display.
- Quality/performance mode selector.
- Preview status.
- Degraded-mode warning.
- Local benchmark summary.
- Cancellation/progress state.

Do not make low-latency preview the default export path. The UI should make it clear when preview output differs from export output.

## Fakes and test doubles

Add to `tests/Trackdub.TestDoubles/`:

- `FakePortraitPreviewRenderer` or fake low-latency provider.
  - Simulates chunk processing.
  - Records chunk count and timing options.
  - Configurable runtime unavailable/hardware insufficient/license gate/failure.

- Fake benchmark service.
  - Returns deterministic throughput and memory fixtures.

## Acceptance criteria

V26 is accepted when:

- Providers can declare low-latency capability flags.
- Preview path is separate from export-quality rendering.
- Start/stop preview works with fake provider.
- Runtime unavailable skips preview with exact status.
- Hardware insufficient skips or degrades with warning.
- License gate blocks preview.
- Non-commercial preview marks contamination when persisted.
- Benchmark harness records local throughput/memory fixture data.
- UI distinguishes preview-ready, degraded, skipped, cancelled, and failed.
- Normal offline export does not depend on realtime preview.

## Non-goals

- No hard dependency on realtime rendering for normal export.
- No cloud streaming service.
- No default live avatar mode.
- No guarantee that experimental providers are production-ready.
- No silent use of preview settings for final export.
- No non-commercial preview in commercial mode.

## Risks

- Realtime aspirations can destabilize reliable offline rendering.
- GPU memory pressure may create confusing failures.
- Preview quality may differ significantly from export quality.
- Chunk boundaries can cause temporal discontinuities.
- Benchmarks can be misleading if they ignore preprocessing/compositing/encoding.
- Users may interpret experimental preview as final quality.

## Tests

Fast tests:

- Provider capability flags parsed correctly.
- Unsupported provider returns `SkippedProviderUnsupported`.
- Runtime unavailable returns `SkippedRuntimeUnavailable`.
- Hardware insufficient returns `SkippedHardwareInsufficient` or degraded status.
- License gate blocks preview.
- Start/stop preview transitions status correctly.
- Cancellation preserves stable state.
- Preview artifact metadata differs from export artifact metadata.
- Non-commercial persisted preview marks contamination.
- Benchmark service records deterministic fixture values.

Integration/benchmark tests:

- Real provider benchmark only after license/runtime review.
- Chunk-boundary preview test.
- GPU memory stress test.
- Export-vs-preview quality/performance comparison.

## Agent implementation prompt

```text
Implement V26 low-latency portrait preview infrastructure without making normal export depend on it.

Read AGENT_CONTEXT.md first. Search the repository for portrait provider, identity pack, artifact, benchmark, runtime readiness, and UI progress patterns.

Add provider capability flags for low-latency preview, PortraitPreviewRequest, PortraitPreviewResult, PortraitPreviewStatus, preview renderer/application service, chunked audio input model, local benchmark harness, GPU readiness checks, degraded-mode warnings, and cancellation/resume behavior.

Add fake low-latency provider and fake benchmark service in tests/Trackdub.TestDoubles. Fakes must simulate chunk processing and support unsupported provider, runtime unavailable, hardware insufficient, license gate, degraded, cancellation, and failure paths.

Keep preview rendering separate from export-quality rendering. Do not wire real providers unless explicitly requested and manifests/runtime checks are reviewed. Do not make realtime preview a prerequisite for offline export.
```

---

# Implementation sequencing recommendation

Agents should not attempt V22-V26 as one giant implementation. Use bounded slices.

Recommended sequence:

1. V22 fake-backed architecture.
2. V22 conservative stretch service.
3. V22 real commercial ONNX aligner proof-of-concept.
4. V22 non-commercial MMS provider gated behind non-commercial mode.
5. V23 fake-backed lip synthesis architecture.
6. V23 face detector/landmark/pose fake and optional real detector proof.
7. V23 experimental MuseTalk provider spike behind manifest gate.
8. V24 portrait animation API and fake provider.
9. V25 identity pack governance.
10. V26 low-latency preview infrastructure.

Do not build the nuclear locomotive before the rails, switches, and warning lights exist.

# Agent checklist for every milestone

Before coding:

- Read `AGENT_CONTEXT.md`.
- Search the repo for existing stage/provider/artifact/manifest/test patterns.
- Confirm project dependency direction.
- Identify state owner.
- Identify pipeline order.
- Identify artifacts to preserve.
- Identify required manifest gates.
- Identify commercial/non-commercial/experimental lane.

During coding:

- Add fakes first.
- Add status enums/results before UI.
- Add manifest checks before real provider wiring.
- Use immutable execution snapshots.
- Preserve prior artifacts on skip/failure.
- Log exact reasons.
- Avoid UI-owned business logic.
- Avoid inference in `App`.
- Avoid persistence in view models.

Before finishing:

- Run relevant tests.
- Add disabled/skip/failure tests.
- Verify non-commercial blocked in commercial mode.
- Verify missing model does not appear ready.
- Verify artifacts are preserved on skip/failure.
- Verify project contamination metadata when applicable.
- Summarize license/model impact.

# Final strategic pitch

Trackdub should evolve from audio-accurate dubbing to visually credible dubbing in layers.

V22 earns timing truth without touching pixels. It aligns dubbed audio to the source speaker’s mouth cadence and gives later stages better audio to consume.

V23 repairs mouth motion in original footage. It keeps the source video authoritative and modifies only safe face/mouth regions, with hard skip paths for bad face evidence, license gates, or runtime gaps.

V24 opens a separate generated-portrait branch. It does not pretend portrait animation is the same thing as lip repair. It gives Trackdub a clean interface for synthetic presenters and avatar workflows.

V25 makes identities reusable and governable. It adds provenance, consent, provider dependencies, commercial eligibility, and non-commercial contamination metadata so generated-person workflows do not become a licensing fog machine.

V26 explores low-latency preview only after the offline system is sane. It keeps realtime experimentation separate from reliable export.

Commercial mode stays conservative, manifest-gated, and boring in the best possible way. Non-commercial mode exposes the research zoo with hard restrictions. Experimental mode absorbs the GPU-heavy weird stuff without contaminating the stable product path.


