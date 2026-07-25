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
