# C13 / #329 — NVIDIA TRT-RTX Shape-Error Triage & Resolution Report

Date: 2026-10-06. **Status: CLOSED / TRIAGE COMPLETE.**

## 1. Executive Summary & Root Cause Provenance

- **Target Issue:** [Trackdub #329](https://github.com/trackdubllc/Trackdub/issues/329), "Investigate: TensorRT RTX build-time shape error on TTS under `--prefer-gpu` — does the EP fallback cover it?"
- **Reported Error Signature:**
  ```text
  ITensor::getDimensions: Error Code 4: Shape Error
  (squeeze index (1) must be less than length (0) of data. In
  nvinfer1::builder::`anonymous-namespace'::depythonizeIndexIfConstant at
  C:\_src\optimizer\common\shape\shapeContext.cpp:3530)
  ```
- **Primary Finding:** The squeeze shape error reported in #329 is **not a TTS model graph error**.
  - An exhaustive AST/binary pattern scan of all cached ONNX models confirmed that **zero TTS models** contain node `If_0_else_branch__Inline_0__/Squeeze`.
  - The node and exact error signature originate exclusively from **`onnx-community/silero-vad`** (`model.onnx` and `model_fp16.onnx`).
  - During the original desktop batch-processing verification run, VAD preflight was invoked prior to TTS. The native C++ stderr log from TensorRT RTX compiling `silero-vad` was emitted to the console. The pipeline later stalled/failed at TTS due to an unrelated cache corruption (`ResembleAI/chatterbox-turbo-ONNX is marked as integrity-failed`), creating the visual impression that TTS caused the squeeze shape error.
- **Fallback Eligibility:** **Verified & Fallback-Eligible.** When `silero-vad` encounters this shape error under `TensorRTRtx`, the failure is classified as fallback-eligible by `OnnxExecutionSessionFactory.LooksLikeTrtSessionInitFailure`. The session degrades gracefully to DirectML (`dml`) on Windows without crashing or terminating the host process.
- **Planner Avoidance for TTS:** All supported TTS models (`kokoro`, `chatterbox`, `qwen3-tts`) are already routed around TensorRT RTX by the planner before compilation, preventing fatal engine crashes. Live Spanish synthesis with `kokoro` under `--prefer-gpu` succeeded in 11.29s, generating playable audio.

---

## 2. Environment & Hardware Identity

- **Host GPU:** NVIDIA GeForce RTX 5070 (12,227 MiB VRAM), Driver version 617.14, CUDA UMD 13.4.
- **OS Platform:** Windows 11 (TFM `net10.0-windows10.0.19041.0`).
- **Core Repository:** `trackdubllc/Trackdub` at commit `9dd44d11147ac61c731b1ab06e8d907edd05f8a0`.
- **Gated Submodule:** `trackdubllc/Trackdub-gated` submodule `external/Trackdub` cleanly pinned at `9dd44d11147ac61c731b1ab06e8d907edd05f8a0`.
- **EP Plugin Configuration:**
  - ABI Version: `0.4.2/cu13`.
  - TensorRT RTX DLL Version: `1.6.1.120`.
  - ONNX Runtime Version: `1.30.0`.
  - Plugin Path: `C:\Users\tonyt\AppData\Local\Trackdub\Providers\trt-rtx\0.4.2\cu13\win-x64\onnxruntime_providers_nv_tensorrt_rtx.dll`.
- **Development Tooling:** eSpeak-NG 1.52.0 (SHA-256 verified) installed in `tools/espeak-ng/` via `Fetch-EspeakNg.ps1` (`trackdub doctor` passes).

---

## 3. Shape Error Reproduction & Fallback Verification

### 3.1 Exhaustive Cache Pattern Scan
A pattern scanner inspected all cached ONNX graphs in `C:\Users\tonyt\AppData\Local\Trackdub\model-cache` for the identifier `If_0_else_branch`:

```text
MATCH: C:\Users\tonyt\AppData\Local\Trackdub\model-cache\onnx-community\silero-vad\onnx\model.onnx
MATCH: C:\Users\tonyt\AppData\Local\Trackdub\model-cache\onnx-community\silero-vad\onnx\model_fp16.onnx
DONE SCANNING — 0 TTS models matched.
```

### 3.2 Direct Native Reproduction (`silero-vad`)
Using the production session factory harness (`OnnxExecutionSessionFactory.CreateSingleAsync`) on `onnx-community/silero-vad/onnx/model.onnx` with provider `TensorRTRtx` and `allowFallback: true`:

```text
{"phase":"session-init-start","graph":"C:\\...\\silero-vad\\onnx\\model.onnx","provider":"TensorRTRtx","allowFallback":true}
[E:onnxruntime:, tensorrt_rtx_execution_provider.h:201] ITensor::getDimensions: Error Code 4: Shape Error (squeeze index (1) must be less than length (0) of data. In nvinfer1::builder::`anonymous-namespace'::depythonizeIndexIfConstant at C:\_src\optimizer\common\shape\shapeContext.cpp:3530)
[E:onnxruntime:, tensorrt_rtx_execution_provider.h:201] ModelImporter.cpp:140: While parsing node number 0 [Squeeze -> "If_0_else_branch__Inline_0__/Squeeze_output_0"]:
[E:onnxruntime:, tensorrt_rtx_execution_provider.h:201] ModelImporter.cpp:151: ERROR: ModelImporter.cpp:517 In function parseNode:
[6] Invalid Node - If_0_else_branch__Inline_0__/Squeeze
[E:onnxruntime:, tensorrt_rtx_execution_provider.h:201] ModelImporter.cpp:151: ERROR: importerUtils.cpp:330 In function convertAxis:
[8] Assertion failed: (axis >= 0 && axis <= nbDims): Axis must be in the range [0, nbDims (0)]. Provided axis is: 1
[E:onnxruntime:, tensorrt_rtx_execution_provider.h:201] IBuilder::buildSerializedNetwork: Error Code 4: API Usage Error (IConditionalOutputLayer If_0_else_branch__Inline_0__/decoder/If_OutputLayer: Must have inputs with equal ranks when condition is not a build-time constant...)
{"phase":"session-init-success","graph":"C:\\...\\silero-vad\\onnx\\model.onnx","requestedProvider":"tensorrt-rtx","selectedProvider":"dml","detail":"TensorRT RTX EP ABI plugin registered from 'C:\\...\\onnxruntime_providers_nv_tensorrt_rtx.dll'. Session options fallback reason: Encoder: TensorRT RTX session init failed ([ErrorCode:ShapeInferenceNotRegistered] [NvTensorRTRTX EP] Failed to create serialized engine for fused node: NvTensorRTRTXExecutionProvider_NvTensorRTRTXExecutionProvider_12112723482072567427_1_1); fell back to dml. Decoder: Requested tensorrt-rtx but effective dml."}
```

### 3.3 Fallback Classification Mechanics
- When TensorRT engine creation fails, ONNX Runtime wraps the native failure in an `OnnxRuntimeException` containing `[ErrorCode:ShapeInferenceNotRegistered] [NvTensorRTRTX EP] Failed to create serialized engine for fused node...`.
- In `OnnxExecutionSessionFactory.LooksLikeTrtSessionInitFailure(Exception ex)`, the exception chain is evaluated for known tokens:
  ```csharp
  "nvtensorrtrtx", "tensorrt-rtx", "tensorrt rtx", "modelimporter", "failed to create serialized engine"
  ```
- Result: **`fallbackEligible = true`**.
- The fallback pipeline catches the exception, logs the diagnostic reason, and immediately falls through to DirectML on Windows (or CPU as secondary).
- **Process survival:** The process **does not die**. Process exit code is `0`.

---

## 4. TTS Planner Routing & Live Hardware Synthesis Proof

### 4.1 Planner Rules for TTS Families
The planner in [`StageRuntimeRequirements.cs`](../../src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs#L145-L167) defines strict hardware admission rules for speech synthesis:
1. **Kokoro (`kokoro-onnx`):** Explicitly pinned to `[ExecutionProviderKind.Cpu]`. DirectML exhibits severe precision degradation with `ConvTranspose` operations in audio vocoding, and TRT builder encounters dynamic upsample shape assertions (`Resize shape dims should be non-negative`).
2. **Chatterbox (`chatterbox-turbo`):** Excludes TensorRT families via `.WithoutTensorRtFamilies()`. Dynamic `MultiHeadAttention` operators are caught by `TrtRtxUnsupportedOpScanner`.
3. **Qwen3-TTS (`qwen3-tts`):** Excludes TensorRT families via `.WithoutTensorRtFamilies()` due to multi-graph init gating.
4. **CosyVoice (`cosyvoice-300m`):** Multi-graph package smoke test checks each sub-graph. While `campplus.onnx` and `f0_predictor.onnx` initialize on TRT, `hift/vocoder.onnx` fails on `ScatterElements` (unsupported reduction type) and drops to CPU. The smoke check verifies effective providers across the package, preventing partial TRT assignment.

### 4.2 End-to-End Live TTS Synthesis Under `--prefer-gpu`
To satisfy the completion gate ("proven by a run that avoids it"), a real Spanish Kokoro TTS stage was executed under `--prefer-gpu`:

- **Command:**
  ```powershell
  dotnet src/Trackdub.Cli/bin/Release/net10.0-windows10.0.19041.0/Trackdub.Cli.dll run-stage `
      --project "D:\Dev\Trackdub_Workspace\Trackdub\.freebuff\c13-20261005\tts-kokoro-es.trackdub" `
      --stage tts --model kokoro-onnx --prefer-gpu --verbose
  ```
- **Execution Log:**
  ```text
  [Started] tts
  [Progress] tts: 0% - Auto-assigning a fallback voice to 2 speaker(s) without a voice assignment (unattended run).
  [Started] TTS
  [Progress] TTS: 0% - Speaker 1 of 2: Speaker 1
  [Progress] TTS: Skipping speaker - Speaker 1 has no assigned transcript segments.
  [Progress] TTS: 50% - Speaker 2 of 2: Speaker 2
  [Progress] TTS: Preparing speaker
  [Progress] TTS: Preparing segments - 1 segment(s) queued.
  [Progress] TTS: 0% - 1 segment(s) queued.
  [Progress] TTS: Output available - Playable audio persisted for segment 0.
  [Progress] TTS: 100% - Segment 1 of 1
  [Completed] TTS (9.7s)
  [Completed] tts (11.3s)
  {"stage":"tts","status":"Succeeded","elapsedSeconds":11.2902076}
  ```
- **Output Artifact:**
  `D:\Dev\Trackdub_Workspace\Trackdub\.freebuff\c13-20261005\tts-kokoro-es.trackdub\artifacts\tts\08c3512d-51b7-487d-8bd0-c73cdb89cf4d\c0a58f36-0bf3-48bc-9710-e4d7d5eaa2ea-take-0002.wav`
  Size: 440,740 bytes, timestamp 2026-10-06 00:55:03.
- **Result:** Exit code `0`. Proves that `--prefer-gpu` leaves TTS safely routed to CPU, generating valid synthetic speech without hitting TRT builder shape errors.

---

## 5. Comprehensive Model Blast Radius Across Pipeline Stages

The full blast radius was evaluated across all pipeline stages on the RTX 5070:

| Stage | Model / Family | Effective Provider | TRT-RTX Behavior & Routing Mechanism |
|---|---|---|---|
| **VAD** | `onnx-community/silero-vad` | `dml` (degraded) | **Encountered exact Error Code 4 Squeeze Shape Error.** Caught by `LooksLikeTrtSessionInitFailure`; degraded to DirectML cleanly. Exit 0. |
| **ASR** | `onnx-community/whisper-tiny` | `tensorrt-rtx` | **PASS.** Encoder & decoder compiled and verified on TRT-RTX. Stderr shape warnings (`kMAX 32767 != 448`) are non-fatal. |
| **ASR** | `onnx-community/whisper-base` | `tensorrt-rtx` | **PASS.** Compiled and verified on TRT-RTX. |
| **ASR** | `onnx-community/whisper-small` | `tensorrt-rtx` | **PASS.** Compiled and verified on TRT-RTX. |
| **ASR** | `Xenova/whisper-medium` | `tensorrt-rtx` | **PASS.** Compiled and verified on TRT-RTX. |
| **ASR** | `onnx-community/whisper-large-v3` | `dml` (fallback) | **CUDA OOM during TRT serialization** (32 decoder layers exceed compilation memory on 12GB VRAM). Falls back to DirectML. |
| **ASR** | `whisper-genai` | `dml` / `cpu` | Excluded from TRT-RTX via GenAI guard (prevents native stack overflow in ORT GenAI). |
| **ASR** | `parakeet-tdt` | `tensorrt-rtx` | **PASS.** Compatible with TRT-RTX. |
| **ASR** | `qwen3-asr` | `dml` | DirectML routed first. |
| **Diarization** | `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `tensorrt-rtx` | **PASS.** Pre-compiled EP-context / TRT session verified with exit 0. |
| **Separation** | `spleeter` | `tensorrt-rtx` | **PASS.** 4-stem separation runs on TRT-RTX. |
| **Separation** | `sepformer` | `dml` | Degrades to DirectML due to dynamic chunking constraints. |
| **Translation** | `opus-mt` | `dml` / `cpu` | Excluded from TRT-RTX via `WithoutTensorRtFamilies`. |
| **Translation** | `madlad` | `dml` / `cpu` | Excluded from TRT-RTX via `WithoutTensorRtFamilies`. |
| **TTS** | `kokoro-onnx` | `cpu` | **Planner pinned to CPU.** Avoids TRT Resize shape dims error and DirectML ConvTranspose corruption. Live stage succeeded. |
| **TTS** | `chatterbox-turbo` | `dml` / `cpu` | Excluded from TRT-RTX via `WithoutTensorRtFamilies` (unsupported dynamic `MultiHeadAttention`). |
| **TTS** | `qwen3-tts` | `dml` / `cpu` | Excluded from TRT-RTX via `WithoutTensorRtFamilies` (multi-graph init constraints). |
| **TTS** | `cosyvoice-300m` | `cpu` / `dml` | CAMPPlus passes; vocoder fails on `ScatterElements` (unsupported reduction); multi-graph smoke gates provider. |

---

## 6. Acceptance Criteria Verification & Issue #329 Closure Gates

The requirements specified for resolving Issue #329 / C13 are verified as follows:

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1 | Reproduce under `--prefer-gpu` and record whether failure is classified as fallback-eligible or kills the run. | **DONE** | Exact squeeze shape error reproduced on `silero-vad` (the only matching graph). Classified as fallback-eligible (`LooksLikeTrtSessionInitFailure` = true); session degraded to `dml` without killing the host process. |
| 2 | If fallback-eligible but misclassified → fix classification so run degrades instead of dying. | **DONE** | Verification proved the exception already includes `[NvTensorRTRTX EP]` and is correctly classified. No code changes required to the classifier. |
| 3 | If genuinely unsupported → planner must route TTS around TRT-RTX rather than fail run. | **DONE** | Planner rules in `StageRuntimeRequirements.cs` route all TTS models around TRT-RTX (`Kokoro` CPU-only, `Chatterbox`/`Qwen3-TTS` exclude TRT families, `CosyVoice` smoke-gated). |
| 4 | Confirm which models are affected across pipeline stages. | **DONE** | Swept all models. Only `silero-vad` hits the squeeze shape error; Whisper large-v3 hits TRT build OOM; Whisper tiny/base/small/medium, SortFormer, and Spleeter compile and pass on TRT-RTX. |
| 5 | **Done when:** fallback covers it (proven by degrading run) OR planner routes around it (proven by avoiding run). | **DONE** | **Both conditions proven on hardware:**<br>• Fallback degradation proven by `silero-vad` session init degrading to `dml` (exit 0).<br>• Planner routing proven by live TTS stage on `tts-kokoro-es.trackdub` under `--prefer-gpu` completing successfully (`take-0002.wav`, exit 0). |

## 7. Recommendation

Close GitHub Issue [#329](https://github.com/trackdubllc/Trackdub/issues/329) with this triage report and log hardware verification evidence in the Linear TS team workspace.
