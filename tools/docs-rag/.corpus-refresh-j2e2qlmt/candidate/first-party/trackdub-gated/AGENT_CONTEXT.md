# AGENT_CONTEXT.md

Root operating context for AI coding agents on Trackdub.

Read before changing code. Conflict with source code? Inspect source, preserve existing architecture unless task explicitly asks migration. Don't invent parallel systems (look simpler in isolation). Trackdub = pipeline product; bugs = broken ownership, readiness, artifacts, stage ordering.

## Working with Tony (read first)

Tony orchestrates agent swarms — sets architecture/vision, agents execute. Expectations:

- **Fast, minimal ceremony.** Push over PRs when says so; don't over-ask.
- **Never fake readiness** (see rule). Catches stubs = trust loss.
- **Honest state always.** Say "not verified" not guess. Prefers accurate status.
- **Scope creep = weakness.** Flag if scope reopens, ask close current first.
- **Git commits:** always `git commit -m` (interactive editor broken).

## Product one paragraph

Cross-platform desktop AI video dubbing (Windows/macOS/Linux). Ingest media → transcripts → translate → voices → TTS → timing → preview/export. Feel practical local tool, not research UI. Values low friction, privacy, smart acceleration, resumable execution, manifest gates, deterministic fallbacks, honest readiness all platforms.

## Central rule: no fake readiness

Never show/return/store/infer capability ready unless ALL prerequisites true.

Readiness not one boolean. Separate when feature depends on models/providers/hardware/binaries/artifacts:

- provider registered
- runtime installed
- external binary available
- model manifest present
- model files downloaded
- checksum verified
- license metadata present
- license reviewed
- commercial mode allowed
- hardware provider available
- stage enabled in snapshot
- prerequisites satisfied
- stage ran
- stage produced usable output
- stage skipped safely
- stage failed

Disabled stage ≠ ran. Skipped stage ≠ succeeded. Provider registered ≠ model installed. Repo license ≠ weights commercial. Duration match ≠ lip-sync quality.

## Repository shape preserve

Verify exact paths before editing. Expected boundaries:

- `src/Trackdub.App.Avalonia/`
  - Active desktop UI (Windows/macOS/Linux). Views, view models, state, panels, segments, commands.
  - Don't own pipeline logic.
  - Don't perform inference/model work.
  - Reflect app state, don't fabricate readiness.

- `src/Trackdub.App/`
  - Retired WinUI shell. This project should not exist in the active source tree or solution.
  - Historical WinUI references in docs/plans are migration clues only. Verify behavior still makes sense, then wire into Avalonia/shared layers.

- `src/Trackdub.Application/`
  - Pipeline orchestration, stage planning, snapshots, project/session, artifact routing, provider selection, manifest/license gates, services.
  - Usually where stages, plans, high-level services belong.

- `src/Trackdub.Inference/`
  - Inference interfaces + provider contracts.
  - Narrow, testable, provider-neutral.
  - Don't leak UI types into inference.

- `src/Trackdub.Inference.Onnx/`
  - ONNX Runtime implementations, adapters.
  - ONNX concerns here, not application.
  - Don't promise ONNX until export/operator/runtime proven.

- `tests/Trackdub.TestDoubles/`
  - Fakes shared by tests.
  - Deterministic, no real audio/video/model/network I/O unless test explicitly integrates.

- Other `tests/*`
  - Unit/integration. Preserve style/conventions.

Don't move code casually. Multi-project touch? Explain why each boundary involved.

Branch + PR discipline: lightweight trunk-based (main).

Create/recommend branch:
- from main
- exactly one: `feature/<kebab>`, `bugfix/<kebab>`, `hotfix/<kebab>`, `chore/<kebab>`
- match: `^(feature|bugfix|hotfix|chore)\/[a-z0-9]+(?:-[a-z0-9]+)*$`

Don't: `test`, `misc`, `updates`, `michael-test-2`

PRs should:
- target main
- single-responsibility
- imperative title (Add, Fix, Revise, Remove, Refactor, Document, Test)
- link issue when exists
- test notes
- architecture impact when crossing boundaries
- prefer squash merge unless explicitly preserve history

## Start any task

Before coding:

1. Search repo existing interface, service, stage, registry, manifest, artifact, fake, test pattern related task.
2. Identify current owner state you mutate.
3. Identify pipeline stage ordering, prerequisite state.
4. Identify how artifacts named, stored, preserved, resumed.
5. Identify stable/non-commercial/experimental.
6. Add/update fakes before real providers.
7. Test success, disabled, skipped, missing-prerequisite, failure paths.

Don't create top-level abstraction unless no existing seam fits. Most mistakes = duplicate abstractions.

## Working style agents

Correct fix for real problem. Small, surgical changes good when solve cleanly, but don't choose smallest patch preserving broken architecture/state drift/known failure modes.

Issues while working:

- Fix nearby defects, flaky tests, stale docs, debt when safe + improves change.
- Major/architectural/risky/outside ownership? Don't derail task. Concise code note + handoff note (let Tony decide).
- Don't ignore serious findings because pre-existing/adjacent.
- Keep cleanup reviewable. Separate unrelated fixes conceptually.

Avoid:
- broad rewrites
- architecture migrations hidden in feature work
- new global state
- static service locators
- UI-owned business logic
- model downloads in tests
- network in tests
- runtime `pip install`
- swallow exceptions without structured status
- return success with missing artifacts
- delete/overwrite original artifacts
- hardcode absolute machine paths
- add giant binaries
- silently enable non-commercial/experimental

Uncertain? Preserve existing + add explicit skipped/blocked status.

## Pipeline + stage rules

Explicit, ordered, resumable.

Each should have:

- stable id
- clear prerequisite contract
- immutable snapshot
- declared inputs
- declared outputs
- per-segment/artifact status
- structured skip reasons
- structured failure reasons
- artifact preservation rules
- enabled/disabled tests
- missing-prerequisite tests
- fallback tests

Pipeline toggle = config, but execution = immutable snapshot at run start. Don't let mutable UI change meaning.

Stage may skip + preserve earlier artifact. Valid only if status says skipped + reason logged.

## Artifact rules

Original media + prior successful artifacts sacred. Don't overwrite in-place.

Every generated artifact:

- record what created it
- record source ids/paths
- record provider id/version when relevant
- record stage id
- record non-commercial/experimental model use
- record metadata to resume/explain
- distinguish skipped/fallback from new

Stage fails? Preserve previous usable unless task says otherwise.

Audio/video manipulation? Prefer existing FFmpeg services + artifact patterns. Don't spawn ad hoc FFmpeg from UI.

## Provider + model governance

Every real provider = manifest. Don't use real files without manifest metadata.

Useful manifest:

- `model_id`
- `task`
- `engine_family`
- `capabilities`
- `tier`
- `license`
- `source_url`
- `revision`
- `sha256`
- `commercial_allowed`
- `commercial_use_verified`
- `redistribution_allowed`
- `requires_attribution`
- `requires_user_consent`
- `voice_cloning`
- `aliases`
- `root_path`
- `benchmark_entry`
- `variants`

Never treat repo license as enough. Code/weights/dependencies/cards/datasets all different terms.

### Model lanes

Three explicit lanes.

#### Commercial

Default product-safe.

Requirements:

- code license verified
- weight license verified
- dependencies reviewed
- source URL recorded
- SHA-256 recorded/verified
- `commercial_allowed: true`
- `commercial_use_verified: true` (product gate; `MODEL_LICENSE_POLICY.md`)
- no known non-commercial training-data restriction blocking product

Prefer conservative, clean, durable over exciting research.

`commercial_allowed: true` = license evidence, not final gate.
`commercial_use_verified: true` = both confidence + integrity verified. Don't flip without audit under `docs/internal/model-audits/`. `CommercialSafeMode` maps.

#### Non-commercial

Not shipping lane.

Requirements on import/future:

- `commercial_allowed: false`
- visible warning
- contamination metadata
- blocked from commercial export

Demucs/HTDemucs = non-commercial stem-separation only. Research eval outside app, never product path.

Don't add toggle/override laundering project back clean after use.

#### Experimental

Unstable, GPU-heavy, Python/CUDA-first, partially proven, non-ONNX.

Can be commercial-candidate or non-commercial. Runtime/quality/packaging not stable.

Requirements:

- visible label
- explicit runtime checks
- clear fail/skip reasons
- don't block stable paths
- no fake readiness

## Hardware + runtime rules

Cross-platform. Windows/macOS/Linux = targets, not optional. Use portable .NET/Avalonia APIs by default. Guard unavoidable platform-specific with explicit Windows/macOS/Linux.

ONNX + acceleration desirable, don't overpromise. Provider not "GPU-ready" until provider loads/runs graph on current machine.

Windows: **TensorRT RTX** uses the standalone ONNX Runtime EP ABI plugin (`NvTensorRTRTXExecutionProvider`) registered from a validated plugin DLL bundle. **Windows ML** remains the Windows ONNX surface for DirectML and certified catalog EPs (MIGraphX/OpenVINO/QNN/VitisAI). Prefer TRT RTX plugin or catalog EPs where model/stage allow; **DirectML** legacy GPU fallback. See [ADR-0002](docs/adr/ADR-0002-windows-ml-provider-strategy.md) and [docs/internal/tensorrt-rtx-ep-abi-plugin.md](docs/internal/tensorrt-rtx-ep-abi-plugin.md). Registration, smoke-test, per-model lists = separate readiness.

General:

- ONNX realistic audio models, classifiers, face detectors, landmarks.
- ONNX + Windows ML/DirectML video synthesis = future export proof, not assumed.
- macOS/Linux = keep portable buildable, platform-appropriate seams.
- Python/CUDA acceptable experimental.
- CPU video synthesis fallback = usually not production, show perf warning.
- Runtime checks before expensive work.
- Hardware = structured state, not GPU names.

Don't install runtimes/Python/files/binaries in normal tests.

## UI rules

UI reflect app state. Not pipeline truth source.

UI changes:

- heavy work out of view models
- avoid blocking UI thread
- fine-grained statuses
- distinguish disabled/skipped/failed/succeeded
- show non-commercial/experimental warnings
- show missing prerequisites clear
- don't imply ready because toggle exists
- don't hide preserved fallback behind success

Segment status important. Some segments succeed = UI say partial, not success.

## Testing rules

Deterministic + cheap unless marked integration/performance.

Fakes first.

New stage/provider, test:

- enabled
- disabled
- missing manifest
- non-commercial blocked commercial mode
- low confidence skip
- runtime unavailable skip
- artifact preservation skip
- failure on exception
- cancellation
- idempotent/resumable if participates

Fakes:

- avoid real model/audio/video/network I/O
- expose counts + inputs
- allow configurable success/fail/skip
- return stable fixtures
- in `tests/Trackdub.TestDoubles/` when shared

FFmpeg/ONNX/CUDA/Python/real media = integration or slower layers.

## Naming + API design

Existing conventions first. If none, prefer:

- interfaces provider boundaries
- immutable records results/status
- `CancellationToken` async work
- explicit result/status over ambiguous bool
- enums stable skip/failure reasons
- provider-neutral `Trackdub.Inference`
- runtime-specific implementations runtime projects
- orchestration `Trackdub.Application`
- projection `Trackdub.App.Avalonia`

Avoid:

```csharp
bool Success;
string? Message;
```

Prefer:

```csharp
Status = SkippedLowConfidence
Reason = "alignment_confidence_below_threshold"
PreservedArtifactId = ...
```

## M22-M26 roadmap

Post-TTS layered strategy.

### M22: Phoneme lip sync audio-level

Improve dubbed timing phoneme/viseme without video touch.

Commercial:

- `facebook/wav2vec2-lv-60-espeak-cv-ft`
- Apache-2.0
- phoneme/token logits
- Trackdub implement CTC alignment

Don't pretend direct phoneme timestamps. Emits logits/tokens; app owns normalization, phonemization, CTC, timing, confidence, mapping.

Interface/provider names:

- `IForcedAligner`
- `OnnxCtcPhonemeAligner`
- `IPhonemeInventoryMapper`
- `IPhonemeTimingPlanner`
- `IPhonemeStretchService`
- `PhonemeTimingPlan`
- `LipSyncStagePlan`

Non-commercial:

- MMS forced alignment ONNX
- CC-BY-NC-4.0
- blocked commercial

Stretch rules:

- don't stretch every phoneme
- clamp ratios
- avoid tiny consonants
- merge tiny regions
- prefer vowels/nuclei
- crossfade boundaries
- preserve final duration
- preserve original atempo if unsafe

Useful statuses:

- `NotRun`, `Aligned`, `Partial`
- `SkippedLowConfidence`, `SkippedNoPhonemes`
- `SkippedInventoryMismatch`, `SkippedUnsafeStretchRatio`
- `SkippedLicenseGate`, `Failed`

### M23: Video lip synthesis original-footage repair

Repair mouth in original. Preserve original authority. Modify face/mouth when quality gates pass.

Commercial:

- MuseTalk 1.5
- best current commercial-candidate
- treat experimental-runtime first
- Python/CUDA, not promised ONNX/DirectML
- dependencies need review

Interface names:

- `ILipSynthesisEngine`
- `PythonMuseTalkLipSynthesisEngine`
- `IFaceDetector`
- `IFaceLandmarkProvider`
- `IFacePoseEstimator`
- `LipSynthesisStagePlan`

Don't name real M23 only `OnnxLipSynthesisEngine`. ONNX deferred export/operator/runtime proven.

Non-commercial/research:

- Wav2Lip: not commercial-clean default
- LatentSync: promising, not default without legal
- EchoMimic/V2: portrait/animation
- Hallo2: portrait candidate

M23 by speaker turn, not whole video. Consume improved audio M22. Optional timing metadata, no symbolic phonemes (models condition audio features).

Statuses:

- `NotRun`, `Synthesized`
- `SkippedNoFace`, `SkippedNonFrontal`
- `SkippedLowConfidence`, `SkippedOccluded`
- `SkippedUnstableCrop`, `SkippedLicenseGate`
- `SkippedRuntimeUnavailable`, `Failed`

### M24: Portrait animation provider API

Separate generated-performance branch.

M23 repair original. M24 generate portrait from identity + audio. Don't collapse.

Names:

- `IPortraitAnimationEngine`
- `PortraitAnimationStagePlan`
- `PortraitAnimationRequest`
- `PortraitAnimationResult`
- `FakePortraitAnimationEngine`

Inputs may:

- single image
- short identity clip
- face crop
- dubbed audio
- optional style/performance

Output = generated portrait, not repaired original.

Most start experimental. Non-commercial = blocked commercial.

### M25: Avatar identity packs + reusable speaker identities

Create, store, assign, reuse across projects (consent, provenance, license, provider, contamination).

Names:

- `AvatarIdentityPack`
- `IAvatarIdentityProvider`

Metadata:

- source type
- user supplied
- consent confirmed
- license notes
- provider id/version
- model dependencies
- created date
- checksum
- commercial allowed
- contamination state

No bundled celebrity/public avatars. No automatic consent.

### M26: Low-latency portrait preview + realtime rendering

Low-latency + streaming-oriented without destabilizing export.

Experimental.

Add:

- realtime flags
- benchmark harness
- session telemetry
- preview-only output
- GPU checks
- degraded warnings
- cancellation/resume
- export-quality vs preview separation

Don't make export depend realtime.

## Special: MuseTalk/V100 perf claims

Model claims 30fps+ V100? = constrained face/mouth, not 1080p/4K end-to-end.

Full pipeline:

- frame extraction
- face detection
- landmarks
- crop tracking
- audio features
- inference
- compositing
- reinsertion
- encoding
- bookkeeping
- UI/progress

Benchmark full before claiming realtime.

MuseTalk-class = CUDA/NVIDIA experimental. 12GB VRAM practical minimum. 8GB limited if supported. CPU fallback not production without benchmark.

## Security, privacy, consent

Trackdub handles user media. Private.

Don't:

- upload unless task explicitly requests + UI clear
- log transcript/audio/video unnecessarily
- store secret tokens in project
- infer consent face/voice identity
- bundle public figures
- silently use non-commercial

Identity/avatar work? Store provenance + consent. Don't erase.

## External research + web

Use web/GitHub/model-cards verify models, licenses, runtimes, hardware.

External info? Record source URLs manifests/notes. Don't rely memory.

Fresh research important:

- MuseTalk, Wav2Lip, LatentSync
- EchoMimic, Hallo2, SadTalker
- MMS alignment, wav2vec2 phoneme
- ONNX Runtime, DirectML/Windows ML
- CUDA, FFmpeg

## Review checklist PR

Before finish, verify:

- No existing seam bypassed.
- Pipeline stage order correct.
- Immutable snapshots where relevant.
- UI don't own pipeline truth.
- Statuses distinguish success/skipped/failed/disabled.
- Artifacts preserved skip/failure.
- Commercial/experimental gates enforced.
- Manifests required real models.
- No model files/binaries accidental.
- No runtime downloads normal tests.
- Fakes deterministic.
- Tests cover disabled, missing-prerequisite, skip, failure, success.
- Logs useful codes, no sensitive content.
- App don't claim unverified readiness.

## Good agent behavior

Careful maintainers.

They:
- inspect first
- subagents bounded independent work parallel/read-only
- reuse seams
- fake-backed architecture before real
- stable/experimental separate
- explicit statuses > implicit success
- preserve artifacts
- honest UI
- tests around fail/skip
- record uncertainty, don't hide

Bad agents = demo builders.

They:
- avoid delegation blocked by result
- UI direct to inference
- duplicate registries
- "ready" because class exists
- non-commercial = normal
- overwrite artifacts
- hide failures
- unverified hardware
- huge files
- skip tests "just plumbing"

Don't be builder.
