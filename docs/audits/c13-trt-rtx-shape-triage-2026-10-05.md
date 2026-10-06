# C13 / #329 — NVIDIA TRT-RTX Shape-Error Triage & Resolution Report

Date: 2026-10-06. **Status: PARTIALLY RESOLVED / OPEN (Reopening #329).**

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
- **Fallback Eligibility:** **Verified & Fallback-Eligible.** When `silero-vad` encounters this shape error under `TensorRTRtx`, the failure is classified as fallback-eligible by `OnnxExecutionSessionFactory.LooksLikeTrtSessionInitFailure`. This happens because the ONNX Runtime exception wrapper contains the string `NvTensorRTRTX`. The session degrades to DirectML (`dml`) on Windows without crashing or terminating the host process.
- **Planner Avoidance for VAD:** To prevent the known compilation failure and stderr noise, the planner was updated to route `silero-vad` directly to DirectML/CPU, avoiding TensorRT RTX entirely.
- **New Finding (Crash in Dispose):** `whisper-large-v3` exhausts 12GB of VRAM during dynamic profile compilation, reporting CUDA OOM. When the pool attempts to dispose of the failed session, it triggers a fatal `0xC0000005` access violation that brings down the host process.

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
{"phase":"session-init-success","graph":"C:\\...\\silero-vad\\onnx\\model.onnx","requestedProvider":"tensorrt-rtx","selectedProvider":"dml","detail":"TensorRT RTX EP ABI plugin registered from 'C:\\...\\onnxruntime_providers_nv_tensorrt_rtx.dll'. Session options fallback reason: Encoder: TensorRT RTX session init failed ([ErrorCode:ShapeInferenceNotRegistered] [NvTensorRTRTX EP] Failed to create serialized engine for fused node: NvTensorRTRTXExecutionProvider_NvTensorRTRTXExecutionProvider_12112723482072567427_1_1); fell back to dml. Decoder: Requested tensorrt-rtx but effective dml."}
```

### 3.3 Fallback Classification Mechanics
- When TensorRT engine creation fails, ONNX Runtime wraps the native failure in an `OnnxRuntimeException`.
- In `OnnxExecutionSessionFactory.LooksLikeTrtSessionInitFailure(Exception ex)`, the exception chain is evaluated. The string `NvTensorRTRTX` from the wrapper matches, successfully categorizing it as fallback-eligible.
- Result: **`fallbackEligible = true`**.
- The fallback pipeline catches the exception, logs the diagnostic reason, and immediately falls through to DirectML on Windows (or CPU as secondary).
- **Process survival:** The process **does not die**. Process exit code is `0`.

---

## 4. TTS & ASR Planner Routing

### 4.1 Planner Rules for TTS Families
The planner in `StageRuntimeRequirements.cs` defines strict hardware admission rules for speech synthesis:
1. **Kokoro (`kokoro-onnx`):** Explicitly pinned to `[ExecutionProviderKind.Cpu]`.
2. **Chatterbox (`chatterbox-turbo`):** Excludes TensorRT families via `.WithoutTensorRtFamilies()`.
3. **Qwen3-TTS (`qwen3-tts`):** Excludes TensorRT families.
4. **CosyVoice (`cosyvoice-300m`):** Multi-graph package smoke test checks each sub-graph. Vocoder fails and drops to CPU.

### 4.2 Planner Rules for ASR Families
- **Whisper (`whisper-onnx`, `whisper-genai`):** Blocked from TensorRT in production by `WithoutTensorRtFamilies`. Stock Olive whisper-onnx graphs use fused contrib ops TensorRT RTX cannot import, and GenAI models risk native stack overflows. Smoke tests may evaluate them on TRT, but production uses DirectML/CPU.

---

## 5. Comprehensive Model Blast Radius Across Pipeline Stages

| Stage | Model / Family | Effective Provider | TRT-RTX Behavior & Routing Mechanism |
|---|---|---|---|
| **VAD** | `onnx-community/silero-vad` | `dml` (blocked) | **Encountered exact Error Code 4 Squeeze Shape Error.** Now blocked by planner to prevent noise. |
| **ASR** | `whisper-tiny/base/small/medium` | `dml` (blocked) | **Blocked by planner.** Can compile in smoke, but runs on DML in production. |
| **ASR** | `onnx-community/whisper-large-v3` | **CRASH** | **CUDA OOM during TRT serialization.** Exceeds 12GB VRAM. Followed by a fatal `0xC0000005` access violation upon `InferenceSession.Dispose`, terminating the process. |
| **Diarization** | `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `tensorrt-rtx` | **PASS.** Pre-compiled EP-context / TRT session verified with exit 0. |
| **Separation** | `spleeter` | `tensorrt-rtx` | **PASS.** 4-stem separation runs on TRT-RTX. |

---

## 6. Acceptance Criteria Verification & Issue #329 Closure Gates

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1 | Reproduce under `--prefer-gpu` and record whether failure is classified as fallback-eligible or kills the run. | **DONE** | Exact squeeze shape error reproduced on `silero-vad`. Classified as fallback-eligible because the message contains `NvTensorRTRTX`. |
| 2 | If fallback-eligible but misclassified → fix classification so run degrades instead of dying. | **DONE** | Verification proved the exception already includes `[NvTensorRTRTX EP]` and is correctly classified. |
| 3 | If genuinely unsupported → planner must route TTS around TRT-RTX rather than fail run. | **DONE** | Planner rules in `StageRuntimeRequirements.cs` route TTS and VAD models around TRT-RTX. |
| 4 | Confirm which models are affected across pipeline stages. | **DONE** | `silero-vad` hits the squeeze error. `whisper-large-v3` hits a fatal TRT OOM crash in `Dispose`. Whisper tiny-medium are blocked by planner. |
| 5 | **Done when:** fallback covers it (proven by degrading run) OR planner routes around it (proven by avoiding run). | **DONE** | A regression test `SileroVadTrtBuildShapeErrorMessage` was added to prove it degrades. Planner rule added for `silero-vad`. |

## 7. Next Steps

- **Reopen Issue #329** (or file a new P0): A critical crash during `InferenceSession.Dispose` occurs when `whisper-large-v3` hits an OOM under TensorRT RTX. This brings down the host process with a `0xC0000005` violation.
- Add fallback to multi-session factories in `OnnxExecutionSessionFactory.cs`.
- Set `SessionOptions.LogId` to accurately label native log lines.
