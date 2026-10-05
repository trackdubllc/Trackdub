# C13 / #329 — NVIDIA first-pass shape-error triage

Date: 2026-10-05. **Not acceptance / do not close #329.** No production code, provider pins, model artifacts, or performance settings were changed. No performance work was performed.

## Environment and issue provenance

- Issue: [Trackdub #329](https://github.com/trackdubllc/Trackdub/issues/329), OPEN, no comments when retrieved through GitHub MCP. Original core: `36fae7d`; Windows, RTX 5070. The issue does **not** name the TTS model, variant, graph, or managed exception/process exit code.
- Checked-out and rebuilt core: `9dd44d11147ac61c731b1ab06e8d907edd05f8a0`, branch `main`. Working tree clean at start. Built Windows CLI in Release, 0 warnings/errors. DLL product version includes this exact SHA.
- Available local hardware: NVIDIA GeForce RTX 5070, 12,227 MiB VRAM, driver 617.14, CUDA UMD 13.4. No NVIDIA access blocker.
- Plugin readiness: registered, license already accepted, hardware eligible, `NvTensorRTRTXExecutionProvider` visible; EP ABI **0.4.2/cu13**, actual TensorRT-RTX DLL version **1.6.1.120**, ORT package **1.30.0**.
- Runtime registration is not treated as evidence that any model ran. Independent model results follow.
- Local evidence and copied scratch projects: `.freebuff/c13-20261005/`. Settings were copied locally for isolation; do not publish those copies. The repository was shared with unrelated running tests; no unrelated processes were stopped.

Original reported native log (not reproduced exactly in this pass):

```text
ITensor::getDimensions: Error Code 4: Shape Error
(squeeze index (1) must be less than length (0) of data. In
nvinfer1::builder::`anonymous-namespace'::depythonizeIndexIfConstant at
C:\_src\optimizer\common\shape\shapeContext.cpp:3530)
```

## Existing fallback and planner

[OnnxExecutionSessionFactory.cs:350–395](../../src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs#L350-L395) wraps single-session creation. Retry requires:

1. `allowTrtInitFallback == true`;
2. selected provider is `TensorRTRtx`;
3. `LooksLikeTrtSessionInitFailure(exception)` returns true.

[Classifier:509–528](../../src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs#L509-L528) walks inner exceptions and accepts TRT-specific messages including `NvTensorRTRTX`, `TensorRT-RTX`, `TensorRT RTX`, `ModelImporter`, kernel/importer evidence. It does **not** match bare `Shape Error`, `squeeze index`, or `ShapeInferenceNotRegistered` by themselves. A catchable build exception naming the TRT provider already qualifies; a native log without a thrown managed exception does not enter this catch. Hard pins disable retry.

[Fallback:426–457](../../src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs#L426-L457) tries DirectML on Windows, then CPU and records the reason. Unsupported contrib graphs can also be bypassed before TRT import.

[Current TTS family rules](../../src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs#L145-L167): Kokoro CPU-only; Chatterbox and Qwen3-TTS exclude TensorRT families; CosyVoice allows TRT, with multi-graph smoke gating. The same family rules are present at original core `36fae7d`. Therefore the original model cannot safely be inferred from the word “TTS,” nor can a family-wide patch be justified from the issue alone.

## Independent NVIDIA experiments

| Target | Interface / result | Meaning |
|---|---|---|
| `onnx-community/whisper-tiny`, default, encoder + decoder | CLI `providers trt-rtx verify`, **exit 0, passed true** | Effective TRT provider checked; encoder and decoder inference ran. Profile-shape warnings are nonfatal for this probe. Not full speech-quality/end-to-end ASR acceptance. |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx`, default | CLI `providers trt-rtx verify`, **exit 0, passed true** | Streaming diarization inference ran with effective TRT provider checked. A valid cached EP-context sibling exists; the production factory may load it, so this is not necessarily a fresh source-graph compiler test. |
| CosyVoice-300M, root `campplus.onnx` entry | CLI verify, **exit 2, passed false** | Primary CAMPPlus probe ran; remaining package graphs were initialized independently of ASR/diarization. Final vocoder landed on CPU and smoke correctly rejected a false TRT pass. Side graphs are init-only, not synthesis acceptance. |
| CosyVoice `hift/f0_predictor.onnx` | Diagnostic invocation of production `CreateSingleAsync`, fallback enabled | **Session initialized on TRT**. Init-only. |
| CosyVoice `hift/vocoder.onnx` | Same diagnostic invocation | Native parser rejected ScatterElements reduction; session **initialized on CPU** without propagating a managed exception. This is ORT graph-placement degradation, not proof that the managed exception classifier fired. |
| Kokoro `onnx/model.onnx` (default) | Same diagnostic invocation, deliberately bypassing CPU-only planner | **Actual TRT builder shape failure reached managed init fallback; CPU session returned successfully.** Exact failure below. No synthesis performed by harness. |
| Chatterbox turbo `conditional_decoder_fp16.onnx` | Same diagnostic invocation | Unsupported-op scanner found `MultiHeadAttention`; **TRT import avoided**, CPU session returned. Init-only. |

The diagnostic harness is local-only in `.freebuff/c13-20261005/graph-probe/`. It reflects into the **freshly built production factory**, does not replace/fake session creation, and prints requested/effective provider plus bootstrap/fallback detail. It has no DI catalog discovery, so DirectML was unavailable there; the recorded retry continued to CPU. This is not a claim that DirectML is unavailable in the normal application.

### Actual Kokoro build-time shape error and degrading session

From `.freebuff/c13-20261005/tts-other-graphs.log`:

```text
IBuilder::buildSerializedNetwork: Error Code 1: Myelin
Error during shape inference of
/encoder/N.1/upsample/Resize_output_0 ...
Error is:
Resize shape dims should be non-negative
```

Production fallback detail:

```text
TensorRT RTX session init failed
([ErrorCode:ShapeInferenceNotRegistered] [NvTensorRTRTX EP]
Failed to create serialized engine for fused node:
NvTensorRTRTXExecutionProvider_NvTensorRTRTXExecutionProvider_10388469194493941920_0_0);
fell back to cpu.
```

The factory returned `requestedProvider: tensorrt-rtx`, `selectedProvider: cpu`; process exit **0**. This proves a **real build-time shape failure** is already classified and degraded in the current single-session factory. It is a different shape error from #329’s squeeze-index error, so it does not close #329.

### Actual CosyVoice unsupported graph

From `.freebuff/c13-20261005/tts-graph-fallback.log` and `tts-cosyvoice-probe.log`:

```text
ModelImporter.cpp:151: ERROR: onnxOpImporters.cpp:7228 In function importScatterElements:
[9] Unsupported reduction type
[TensorRT EP] No graph will run on TensorRT execution provider
```

The vocoder session succeeded with effective **CPU**. The whole-package smoke returned:

```text
Smoke test requested provider 'tensorrt-rtx' but session creation selected effective provider 'cpu'.
```

Thus the cached CosyVoice package is not all-TRT-capable on this pin. Existing multi-graph smoke detects this limitation. Earlier graphs initialized without the reported squeeze-index error.

An extra CLI verify starting at nested `hift/source.onnx` exited 2 with `NoSuchFile` for `hift/campplus.onnx`: the generic verify interface derived the package root from the entry directory. That probe is invalid as whole-package evidence, not a TRT shape failure; the root-entry CAMPPlus probe above exercised the actual package layout.

### Nonfatal ASR shape warnings

Whisper tiny’s TRT decoder logged `Profile kMAX ... 32767 != 448` and `Profile kMIN ... Reshape dimension of -1 has indeterminate solution`, then returned `passed: true`. Do not classify failure from stderr shape vocabulary alone.

## Attempts through `--prefer-gpu` stage execution

All attempts used **copied scratch projects**, never modified the original project.

1. `run-stage --stage tts --model cosyvoice-300m --prefer-gpu` reached TTS but **substituted stock Qwen** because cloning was off. The log says `TTS_CLONE_MODEL_SUBSTITUTED`; a diagnostic stack later shows `Qwen3Tts ... InferenceSession.Run`. It was explicitly operator-stopped (shell-recorded exit 127), **not** a spontaneous native crash or a reproduction of CosyVoice. The stock fallback log misleadingly mentions Kokoro, so stack/model evidence was retained rather than inferring the model from that line.
2. `run pipeline --only tts --force-rerun --model tts:kokoro-onnx --prefer-gpu` against the copied French revision failed normally (exit 2): Bella does not support French. Changing command target language did not change the persisted revision language. No TRT shape failure was reached.
3. Same TTS-only command against a copied Spanish project, `--voice SPEAKER_00:ef_dora`, reached synthesis preparation but failed normally (exit 2): **eSpeak-NG executable missing**. No replacement/fake phonemes were used. Local development acquisition is documented in [tools/espeak-ng/README.md](../../tools/espeak-ng/README.md); the checked-in directory contains the acquisition script/manifest, not the executable. This is a concrete **TTS prerequisite blocker**, not an NVIDIA execution blocker.

No new TTS WAV / completed stage was proven. Prior WAVs in copied projects must not be counted as this run’s output. No failed or stopped attempt is acceptance.

## Verification and artifact identity

- Windows Release CLI build: **pass**, 0 warnings/errors.
- Existing classifier + session-init fallback tests: **8 passed**, 0 failed/skipped. These include injected failures, so they are regression checks, **not hardware closure evidence**.
- Existing TTS planner deny / CosyVoice-allowed tests: **4 passed**, 0 failed/skipped. Also not hardware closure evidence.
- Disposable graph harness: final build **pass**, 0 warnings/errors (initial nullable compile errors repaired before execution).
- All owned experimental dotnet processes ended or were explicitly stopped; no provider experiment left running.
- No production source changes or commits; this report is the only tracked-tree addition. Local scratch evidence retained.
- File-change hooks were unavailable in SDK mode; terminal checks above were run explicitly.

Graph SHA-256s:

```text
Kokoro model.onnx:       8fbea51ea711f2af382e88c833d9e288c6dc82ce5e98421ea61c058ce21a34cb
Whisper tiny encoder:   6642befb640f950d4a8cbbd17834d59e7e75f575b81ccf213e06b050623ab1dd
Whisper tiny decoder:   ab79e3f2a9a3d98f159f853a3172120a38af7eb5f7863d706aa7d39c228f009e
SortFormer source:      82b9c735e1cfc6b36b4ff8a994d9a0573e922d0e80a58a8553b2c58f7aff0c00
CosyVoice vocoder:      63af48b4bf274bc0f645a2a490021895fb89720bad7a7056ae74d954f3b6dae7
```

## Smallest justified next change / remaining closure work

**No classifier widening is justified by this pass.** The observed real builder shape exception already names TRT and is covered. Do not add a blanket `Shape Error` / generic ORT exception catch based only on native log text.

- Recover the original TTS **model/variant/graph and complete exception/exit evidence** from the original batch configuration/log, or reproduce the exact squeeze-index signature with fixed model identity. The issue and available handoffs omit these identifiers.
- If that managed exception is TRT-specific but lacks the current tokens, add only its narrow signature to the classifier and regression coverage, then prove a real synthesis run degrades.
- If it is a native abort or a graph still genuinely incompatible, no catch classifier can save the process: route that specific model/variant/family around TRT **before native smoke/init**, with a reason and live synthesis proof. Existing Kokoro/Chatterbox/Qwen family excludes already do this; CosyVoice’s current smoke rejection is confirmed, but a pre-init family exclusion would only be justified for a confirmed fatal path, not the recoverable ScatterElements limitation alone.
- Acquire/resolve the existing eSpeak-NG development prerequisite and rerun the Spanish Kokoro stage to prove the current CPU-only planner path completes with a **new** WAV. That would verify current Kokoro avoidance, but cannot identify the unnamed original TTS failure by itself.
- ASR/diarization results are scoped to **Whisper tiny and SortFormer**, not all models. Other ASR families/sizes and translation were not swept.

**Mission closure remains unmet:** exact squeeze-index reproduction/classification and successful affected-model TTS stage have not been proven. This report is first-pass evidence, not a diagnosis-only closure.
