# ADR-0017: Out-of-process ORT 1.30 inference worker on Windows

- Status: Proposed
- Date: 2026-10-08
- Decided by: Tony, 2026-10-08 (choice of "split GenAI out of process" over downgrading GenAI or
  dropping DirectML)

## Context

One Windows process loads exactly one `onnxruntime.dll`, and today two consumers need different ones:

| Consumer | Needs | Why |
|---|---|---|
| DirectML stages (VAD, Whisper ONNX, MADLAD, OPUS, separation, ...) | Windows ML ORT 1.24 | DirectML ships only in Windows ML's ORT and the standalone ORT DirectML package, which ends at 1.24.4. ORT 1.30 CPU and GPU builds expose neither `OrtSessionOptionsAppendExecutionProvider_DML` nor a DirectML catalog device. |
| ORT GenAI 0.17.1 (Phi translation, Whisper GenAI, Qwen refinement) | ORT 1.30 | `GenAiNativeCompatibility` rejects a major/minor mismatch, and GenAI's config finalizer can terminate the host on mismatch. |
| Native CUDA EP (Kokoro TTS) | ORT 1.30 GPU build | The CUDA EP provider bridge only exists in the GPU build; Kokoro fails on DirectML (`ConvTranspose` 80070057) and on TensorRT-RTX (Myelin shape inference). |

The desktop resolves the conflict today by copying ORT 1.30 over Windows ML's DLL
(`AlignOnnxRuntimeNativeForGenAi`). Measured on 2026-10-08 through the real pipeline
(controlled-matrix, RTX 5070): with that root, VAD and Whisper ASR request `dml` and run on `cpu`,
with session-options fallback "DirectML catalog execution provider is not visible in
OrtEnv.GetEpDevices(); direct: Unable to find an entry point named
'OrtSessionOptionsAppendExecutionProvider_DML'". With Windows ML's ORT 1.24 root the same stages
run `dml -> dml`. The TensorRT-RTX EP ABI plugin works with either root.

So the shipped desktop silently runs every DirectML stage on the CPU.

## Decision

1. **The application process loads Windows ML's ORT 1.24** (DirectML, Windows ML catalog EPs,
   TensorRT-RTX plugin). The desktop stops overwriting it.
2. **A second .NET process, the ORT 1.30 worker, hosts everything that needs ORT 1.30:** ORT GenAI
   engines and native-CUDA engines. It ships with the application, built from this repository; it
   contains no Python and adds no end-user runtime dependency beyond the CUDA/cuDNN libraries the
   native CUDA route already requires.
3. **Engines stay behind the existing contracts.** The application talks to worker-hosted engines
   through proxy adapters implementing `ITranslationEngine`/`IStreamingTranslationEngine`,
   `IAudioTranscriptionEngine` and `ITtsEngine`. The planner, stage handlers, commit boundary and
   evidence recording do not change per executor.
4. **The application supervises the worker:** start on first use, handshake with a version stamp
   (worker and application must be the same build), crash detection with bounded restarts,
   cancellation forwarding, and shutdown with the session. A dead or mismatched worker is a stage
   failure with a stated reason, never a silent in-process fallback.
5. **Honest readiness.** Worker presence, its ORT/GenAI versions, and the provider each engine
   actually used are reported as stage evidence, the same as in-process engines.
6. **Proven in the harness first:** no route moves to the worker until controlled-matrix evidence
   shows the selected provider and first-use/warm latency through it.

## Relationship to ADR-0016

ADR-0016 scopes a Rust-supervised Python worker for `.pt`-only models. This ADR is narrower: a
same-language .NET process that exists only because two ORT builds cannot share one process. If the
ADR-0016 sidecar ships, its IPC envelope can host these engines too; until then this worker uses
its own minimal protocol.

## Consequences

Positive:

- DirectML works again for every DirectML-routed stage in the desktop.
- GenAI keeps its required ORT 1.30, and Kokoro can use the CUDA EP (~5x faster than CPU on an
  RTX 5070 in a raw ORT probe).

Negative:

- A second executable to build, sign and ship; cross-process calls add serialization cost
  (text in/out for GenAI translation, PCM out for TTS).
- GPU memory is split across two processes, so the session pool's VRAM admission no longer sees
  the worker's allocations.

## Alternatives considered

- **ORT 1.30 GPU build as the single root:** keeps GenAI and CUDA but leaves DirectML (and so
  non-NVIDIA GPUs) unusable.
- **Windows ML 1.24 root plus a GenAI downgrade:** restores DirectML but loses GenAI 0.15-0.17 work
  and keeps Kokoro on the CPU.
