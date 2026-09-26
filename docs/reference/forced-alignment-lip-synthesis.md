# Forced Alignment, Lip Sync, and Lip Synthesis

This is the implementation-fact reference for how Trackdub actually aligns dubbed
audio to source mouth timing and repairs mouth motion in original footage. It
describes the shipped code, not the V22–V26 roadmap (see
`docs/strategy/V22-V26_Visual_Dubbing.md` for the original milestones that seeded
this work). Where this document and the roadmap disagree, this document is
authoritative: it reflects the code in `src/`.

There are three distinct seams, and they must not be collapsed:

| Stage | Milestone | Contract | What it does | Output |
|---|---|---|---|---|
| Forced alignment (lip sync) | M22 / V22 | `IForcedAligner` | Aligns phoneme/word timing of dubbed TTS to source audio; stretches TTS audio to match source mouth cadence | phoneme-aligned audio take (`LipSyncTake`) |
| Lip synthesis | M23 / V23 | `ILipSynthesisEngine` | Repairs mouth motion inside the *original* video, per speaker turn | patched per-turn clip (`LipSynthesisTake`) |
| Portrait animation | M24 / V24 | `IPortraitAnimationEngine` | Generates a new talking portrait from an identity source | generated portrait video |

Lip synthesis repairs original frames; portrait animation generates new
performers. They share model-shaped concerns (faces, landmarks) but are different
abstractions by design.

---

## Forced alignment (lip sync)

### Routing

`IForcedAligner` is implemented by `RoutedForcedAligner`
(`src/Trackdub.Composition/ForcedAlignment/RoutedForcedAligner.cs`), which holds a
list of `IForcedAlignerAdapter`s and selects one per request:

1. If `ForcedAlignmentOptions.PreferredModelAlias` names an available adapter
   (matched on `ModelId` or `ProviderId`, case-insensitive), use it — but only if
   it satisfies the phoneme-timing requirement. If `RequirePhonemeTimings` is set
   and the preferred adapter is word-level only, fall back to rule 2 instead.
2. Else if `Options.RequirePhonemeTimings` is set, pick the first available adapter
   with `SupportsPhonemeTimings == true`.
3. Else pick the first available adapter (DI registration order).

Two routing rules are load-bearing:

- A request that **requires phoneme timings** must never land on a word-level-only
  aligner. If only a word-level aligner is installed, the router returns a
  structured `Skipped` result instead of silently returning zero phonemes.
- The router **never throws** on no-adapter or non-cancellation adapter failure; it
  always returns a `ForcedAlignmentResult` with an explicit `Status` and reason.
  `OperationCanceledException` from an adapter propagates rather than being
  converted to a result.

The router is the only `IForcedAligner`; callers (the `LipSyncStageHandler`) depend
on the interface, never on a concrete adapter.

### Adapters

Two concrete adapters are registered in `CompositionRoot`:

#### `Wav2Vec2CtcForcedAligner` (phoneme-level)

`src/Trackdub.Inference.Onnx/ForcedAlignment/Wav2Vec2CtcForcedAligner.cs`

- Model: `wav2vec2-lv60-espeak-cv-ft-onnx` (HF `onnx-community/wav2vec2-lv-60-espeak-cv-ft-ONNX`,
  Apache-2.0, commercial-verified). `ProviderId = onnx-ctc-phoneme-aligner`.
- Prefers the INT8 ONNX when present, else the FP16 default; requires `vocab.json`.
- Outputs phoneme timings at 20 ms frame resolution (wav2vec2 stride), in the
  `espeak-ipa` inventory. `SupportsPhonemeTimings == true`.
- Pipeline: read mono 16 kHz PCM → ONNX `input_values` → `logits` → per-frame
  numerically-stable log-softmax → `CtcViterbiAligner.Align(...)` → phoneme/word
  timings with confidence.
- Transcript → phoneme sequence: prefers `IGraphemeToPhoneme` (eSpeak) when a
  language code is supplied; otherwise falls back to a crude Latin→IPA grapheme map.
  Words that phonemize to nothing are skipped; a word that yields no vocab tokens
  fails the whole sequence (structured skip, not a crash).
- Confidence: per-phoneme = `exp(mean log-prob over assigned frames)`; overall =
  geometric-mean-of-frame-log-probs clamped to `[0,1]`; word = mean of its
  phonemes. `Status` is `Success` when overall ≥ `Options.MinOverallConfidence`,
  else `Partial` (if `AllowPartial`) or `Failed`.

#### `QwenForcedAligner` (word-level)

`src/Trackdub.Inference.Onnx/ForcedAlignment/QwenForcedAligner.cs`

- Model: `qwen3-forced-aligner-0.6b-q4-onnx` (HF `tonythethompson/Qwen3-ForcedAligner-0.6B-ONNX`,
  Apache-2.0). `ProviderId = onnx-qwen-forced-aligner`.
- Word-level only at 80 ms resolution: 5000 timestamp classes × 0.08 s. Always
  emits empty `Phonemes`; `SupportsPhonemeTimings == false`.
- Language-gated: supports `en zh yue fr de it ja ko pt ru es`. Unsupported
  language → structured skip.
- Pipeline: 16 kHz mono PCM → 128-bin log-mel (`QwenAudioFeatureExtractor`) →
  tokenize transcript with `<timestamp>` slots (`QwenTimestampProcessor`) →
  ONNX `input_ids`/`attention_mask`/`input_features` → `logits` → argmax timestamp
  classes → word timings.

### The CTC Viterbi core

`src/Trackdub.Inference.Onnx/ForcedAlignment/CtcViterbiAligner.cs` is a pure-managed,
blank-aware Viterbi CTC trellis (no I/O, no model dependency). It takes the flat
log-probability matrix `[frames, vocab]` and a phoneme index sequence, and returns
one `(StartFrame, EndFrame, LogProb)` per phoneme. It enforces the CTC blank rule
(repeated labels must consume an intervening blank) and returns empty when no path
reaches every phoneme.

### Lip-sync stage orchestration

`src/Trackdub.Application/LipSync/LipSyncStageHandler.cs` drives the M22/V22 stage:

1. Align the **TTS take** (target-language transcript, `RequirePhonemeTimings`).
2. If a source-audio timing/transcript map is present, extract the source clip and
   align it too (`AllowPartial: true`).
3. `IPhonemeTimingPlanner.PlanStretches(sourcePhonemes, ttsPhonemes, bounds)` builds a
   per-phoneme stretch plan.
4. If **all** ratios are out of bounds, skip with `SkippedUnsafeStretchRatio` and
   preserve the original TTS take.
5. Else `IPhonemeStretchService.StretchAsync(...)` applies the plan; on null duration
   (service-declined) it skips cleanly.
6. Persist the stretched take as a `LipSyncTake` artifact; partial (any clamped
   ratio) → `Partial`, all in-bounds → `Aligned`.

Default stretch bounds: `MinRatio 0.5`, `MaxRatio 2.0`, `PreferredMaxVowelRatio 1.5`.
The handler never lets an alignment/stretch failure overwrite the original TTS take.

---

## Lip synthesis (M23 / V23)

### Engine

`src/Trackdub.Inference.Onnx/LipSynthesis/LatentSyncOnnxLipSynthesisEngine.cs` is the
shipped `ILipSynthesisEngine`.

- Model: `ByteDance/LatentSync-1.6` ONNX (openrail++, commercial-verified).
  `ProviderId = latentsync-onnx`, `EngineFamily = latentsync-diffusion`.
- Requires four ONNX subgraphs, session-pooled via
  `OnnxExecutionSessionFactory.CreatePooledLatentSyncAsync`: `unet.onnx`,
  `vae_encoder.onnx`, `vae_decoder.onnx`, `whisper_encoder.onnx`.
- All face quality gating happens upstream in `LipSynthesisStageHandler`; the engine
  only runs after every guard has passed.

Per speaker turn the engine:

1. Extracts turn frames to `.rgba` (FFmpeg), then the dubbed-audio segment to WAV.
2. Slices a 2.0 s audio window per frame and computes a Whisper-compatible 80-bin
   log-mel (`LatentSyncTensorPreprocessor.ComputeWhisperMelSpectrogram`), runs the
   Whisper encoder to get conditioning embeddings.
3. Encodes the reference frame to latent (`vae_encoder`), adds noise at the top
   DDIM timestep, then runs the deterministic 25-step DDIM denoising loop
   (`DdimScheduler`, `eta=0`) conditioned on the Whisper embeddings via `unet`.
4. Decodes the latent (`vae_decoder`) and pastes the 512×512 result back into the
   full-size RGBA frame at the face box.
5. Reassembles frames (FFmpeg) into a patched per-turn MP4, moved out of the temp
   working dir before the whole directory is deleted.

The engine is **experimental-lane aware**: `IsExperimental` is `true` unless the
manifest entry is `lane == commercial && commercial_use_verified &&
commercial_allowed`. It is currently commercial-verified, but the stage handler still
refuses to run an experimental engine without explicit user opt-in.

### Tensor preprocessing (pure managed)

`src/Trackdub.Inference.Onnx/LipSynthesis/LatentSyncTensorPreprocessor.cs` does all
frame/audio→tensor math without an image library: RGBA→`[1,3,512,512]` CHW
normalization (nearest-neighbour resize), tensor→RGBA paste, and a Whisper-accurate
log-mel spectrogram (400-sample Hann window, 160 hop, 80 mel bins, 3000 frames,
`clamp(0, max-8)+4)/4` normalization). It uses MathNet's FFT.

### DDIM scheduler

`src/Trackdub.Inference.Onnx/LipSynthesis/DdimScheduler.cs` is a pure-C# DDIM with
`scaled_linear` beta schedule, 1000 training timesteps, default 25 inference steps,
`eta=0` (deterministic). It matches the LatentSync 1.6 diffusion config.

---

## Face analysis (quality gates)

Three face providers gate lip synthesis, all registered in
`LatentSyncLipSynthesisRegistration` and consumed by `LipSynthesisStageHandler`
before the engine runs.

### `ScfrdOnnxFaceDetector` (face detection)

`src/Trackdub.Inference.Onnx/FaceAnalysis/ScfrdOnnxFaceDetector.cs`

- Model: `InsightFace/scrfd-500m` ONNX (MIT, commercial-verified).
- Detects the primary face from a single midpoint frame of the turn; NMS with
  IoU 0.4 picks the highest-confidence face. Confidence threshold 0.3 at decode.
- Decodes SCRFD's three-stride (8/16/32) anchor grids; returns a `FaceRegion`.

### `GeometryLandmarkProvider` (facial landmarks)

`src/Trackdub.Inference.Onnx/FaceAnalysis/GeometryLandmarkProvider.cs`

- Model: `InsightFace/2d106det` ONNX (MIT, commercial-verified), 192×192 crop input.
- Requires the face detection first; returns 106 `(x,y)` landmarks.
- Derives `IsStable` (landmark cloud spans ≥10% in both axes) and `MouthOccluded`
  (mouth landmarks 76–105 span <4% of face width).

### `PoseFromLandmarksEstimator` (head pose)

`src/Trackdub.Inference.Onnx/FaceAnalysis/PoseFromLandmarksEstimator.cs`

- Pure-math (no extra model): estimates yaw/pitch/roll from the 2D106 landmarks
  (eye centers for roll, eye-vs-contour midpoint for yaw, eye-vs-contour Y for
  pitch). 2D-only, so pitch is approximate.

### Stage gates and skip reasons

`LipSynthesisStageHandler.ProcessTurnAsync` applies, in order:

1. `SkippedNoFace` — no usable face.
2. `SkippedLowConfidence` — detection confidence < `MinFaceConfidence` (default 0.65).
3. `SkippedNonFrontal` — |yaw| or |pitch| > 30°.
4. `SkippedUnstableCrop` — landmarks missing/unstable.
5. `SkippedOccluded` — mouth occluded.

Stage-level gates (disabled, license, experimental, runtime-unavailable) skip the
whole stage. Skipped turns always preserve original frames. License gate is
independent of experimental opt-in: a non-approved model is always blocked.

---

## Model manifests and lanes

All five models are pinned in
`src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`:

| Model | Engine family | License | Commercial |
|---|---|---|---|
| `wav2vec2-lv60-espeak-cv-ft-onnx` | `onnx-ctc-phoneme-aligner` | Apache-2.0 | verified |
| `qwen3-forced-aligner-0.6b-q4-onnx` | `onnx-qwen-forced-aligner` | Apache-2.0 | verified |
| `ByteDance/LatentSync-1.6` | `latentsync-diffusion` | openrail++ | verified |
| `InsightFace/scrfd-500m` | `scrfd` | MIT | verified |
| `InsightFace/2d106det` | `insightface-2d106` | MIT | verified |

LatentSync is the M23 shipping lane; MuseTalk 1.5 is archived/superseded (see the
manifest `notes` and the audit doc `docs/internal/model-audits/latentsync-1-6-approved.md`
in the gated repo).