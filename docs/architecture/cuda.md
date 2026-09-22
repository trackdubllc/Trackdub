Starting a read-only pass: I'll map Trackdub's GPU/inference stack first, then pull CUDA docs on each topic and mark which ones actually apply to your architecture.

Note: Serena MCP failed to connect this session, so I'm falling back to built-in Grep/Read per CLAUDE.md's escape clause.

Architecture is clear: .NET + ONNX Runtime with TensorRT-RTX EP — CUDA is consumed *through* ORT, not hand-written kernels. Let me look at the hot paths.

Found two things already. Let me look at the decode loops — that's where CUDA Graphs and stream config would bite.

The Nemotron decode loop is the smoking gun — up to 10 `Run()` calls per encoded frame, each rebuilding CPU tensors. Let me check the CUDA graph config and pull docs.

`enable_cuda_graph=1` is set unconditionally for every TRT-RTX session (line 1694). That has strict preconditions — let me verify them against the docs.

Let me verify the retry path before I characterize the error-handling story.

Read-only pass done — 4 doc searches, ~12 files inspected. Here's what matters.

## Framing first

Trackdub never touches CUDA directly. There are no `.cu` files and no driver-API calls — CUDA is consumed **through ONNX Runtime's TensorRT-RTX EP**. That collapses most of your list: topics that require authoring kernels or managing contexts aren't reachable from C#. What *is* reachable is how you configure sessions and shape your call pattern. Three findings there are worth real attention.

---

## 1. `enable_cuda_graph=1` is set unconditionally — and the call pattern violates its preconditions

[OnnxExecutionSessionFactory.cs:1694](src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs:1694) turns CUDA graphs on for **every** TRT-RTX session.

CUDA docs are explicit that graph replay reuses captured node parameters — *including memory addresses*. Changing them requires an explicit graph update:

> "it is common for only node parameters (such as kernel parameters and memory addresses) to change while the graph topology remains the same. For this case, CUDA provides a lightweight 'Graph Update' mechanism..."

That means graph capture needs **stable device buffers across runs**. Your Nemotron decode loop does the opposite — [NemotronAsrGreedyDecoder.cs:100-109](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs:100) allocates a fresh input set every symbol step, and `CloneTensor` ([:248](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs:248)) allocates a new managed array per call. **There is no `IoBinding` anywhere in the repo** — I grepped; the only `OrtValue` usage is `CreateTensorValueFromMemory` over CPU arrays in Qwen's LanguageModel.

So the flag is doing one of three things, none good: silently not capturing (wasted), capturing then replaying against stale addresses (correctness hazard), or re-capturing per run (slower than off). Worth measuring before assuming it's free.

The *shapes* are actually fine for capture — [NemotronAsrEncoderTrtProfiles.cs:33-35](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrEncoderTrtProfiles.cs:33) pins min==max==opt. It's only the addresses that move.

## 2. The encoder cache round-trips ~6 MB through host memory every chunk, unnecessarily

Per chunk, `cache_last_channel` (24×1×56×1024 floats ≈ 5.5 MB) and `cache_last_time` (≈786 KB) are read back to host, cloned element-by-element through an enumerator, then re-uploaded — [:50-52](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs:50).

But `cache_last_channel_next` (output) feeds straight into `cache_last_channel` (input) of the next iteration. It never needs to leave the GPU. With IoBinding you'd ping-pong two device buffers and skip the transfer entirely.

Compounding it: `CloneTensor` uses `foreach (T value in tensor) data[index++] = value` — not `Buffer.BlockCopy`. That's 1.375M individual enumerator steps per chunk on the CPU side, on top of the transfer.

The pinned-memory angle from the docs applies here too — `NamedOnnxValue.CreateFromTensor` over a GC array is **pageable** memory, and the docs note page-locked memory "is required for asynchronous copies" and "also improves performance of synchronous copies." You can't pin a managed array through the NamedOnnxValue API; you'd need IoBinding to get there.

## 3. The decoder loop is launch-bound — textbook CUDA Graphs territory

`MaxSymbolsPerStep = 10` ([:12](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs:12)) means up to 10 `Run()` calls per encoded frame, each doing a tiny LSTM + joint. The docs describe exactly this profile:

> "For a GPU kernel with a short execution time, this overhead cost can be a significant fraction of the overall end-to-end execution time."

Also: `featureExtractor.BuildChunk` runs **synchronously inside the loop** ([:37](src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs:37)), so CPU mel extraction for chunk N+1 never overlaps GPU work for chunk N. That's a cheaper win than anything CUDA-level — just pipeline it.

---

## Triage of the rest

**Mixed precision** — plumbing already exists (`trt-rtx-fp16` in [ModelLabCommand.cs:628](src/Trackdub.Tools/ModelLabCommand.cs:628), per-stage preferences in `StageRuntimeRequirements`). Docs confirm the safety story: bf16/tf32 Tensor Core fragments are "required to be composed with accumulators of `float` type," so fp16 weights with fp32 accumulation is the normal safe path. The gap isn't CUDA knowledge — it's that nothing gates a precision flip on measured WER/MOS.

**Batching** — structurally blocked, not a CUDA problem. Those TRT profiles pin batch=1, T=65 with min==max==opt. One engine, one shape. Can't feed batch>1 without re-exporting profiles. Reasonable for streaming ASR; just means this topic is off the table.

**Concurrent streams** — every engine serializes behind `SemaphoreSlim(1,1)` (Chatterbox, Kokoro, CosyVoice, Qwen3Tts, QwenForcedAligner) plus `ORT_SEQUENTIAL` everywhere. ORT owns its internal streams; you don't get to schedule them. Your concurrency win is stage pipelining (see #3), not CUDA streams.

**Unified Memory — actively wrong for you.** The docs' "Limited Unified Memory Support" section says on Windows: managed memory allocates in CPU memory first, "The CPU must not access managed memory while the GPU is active," and **"Oversubscription of GPU memory is not allowed."** That last one kills the main reason you'd want it (streaming models larger than VRAM). You're Windows-first. Explicit management — what ORT already does — is correct here. Skip this.

**VMM** — driver-API level, not exposed through ORT, single-GPU desktop app. Skip.

**Error handling** — already solid. ADR-0008 classifies `OrtException` codes with retry + circuit breaker, and `DeviceOomExceptionHelper` catches `CUDA error: out of memory`. One thing I could *not* verify: I didn't locate the `RunWithRetry` implementation, so I can't tell whether retry recreates the session or just re-runs. CUDA errors are sticky at the context level — if it only re-runs, that retry can't recover. Worth a look.

**Profiling** — `nsys` attaches to .NET processes fine and would directly confirm or kill the launch-bound hypothesis in #3 (look for gaps between kernels). But ORT's own profiler (`SessionOptions.EnableProfiling`) gives per-node timings as a Chrome trace with zero setup and is higher signal for "which node is slow." Use both. Caveat from the docs on compute-sanitizer: cuBLAS/cuDNN produce known false racecheck positives, so expect noise from ORT internals.

---

## Suggested order

1. Measure whether `enable_cuda_graph=1` is helping at all — if not, that's a one-line fix either way
2. Pipeline mel extraction off the decode loop — cheapest real win
3. IoBinding for the encoder cache ping-pong — biggest win, but a real refactor, and it's also the prerequisite that would make CUDA graphs legitimately work

Nothing was modified — read-only as asked. Want me to publish this as a shareable page, or dig into any one of these?