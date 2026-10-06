# PITCH: Rust inference sidecar for Trackdub (decision-only, not wired into the build)

- Status: **Pitch — do not merge as accepted architecture until reviewed**
- Date: 2026-10-06
- Author: Hermes Agent (drafted with Tony)
- Scope: `docs/plans/` + `tools/rust-sidecar-spike/` sketches only. Nothing in
  `src/` changes, nothing joins any `.slnx`, CI behavior is unaffected.

## 1. Problem

Two pressures are colliding:

1. **Frontier voice models are PyTorch-only.** Chatterbox, CosyVoice, and
   friends publish `.pt` weights first (sometimes only). Our ONNX-first pipeline
   pays the export tax every time: fragile graph conversions, `If`/`Squeeze`
   graphs that won't lower (Silero on TRT-RTX), `-1` reshape profile fights
   (SortFormer), and the TTS stall investigations in `.freebuff/c13-*`.
2. **Python is the worst vehicle for shipping them.** A per-inference
   `python` spawn costs seconds (interpreter + torch import + CUDA context +
   weight load). A resident daemon fixes the per-call cost but still ships a
   multi-GB Python + torch + CUDA-matched runtime to every user — directly
   against the AGENTS.md rule: *no end-user Python, Conda, Docker, or CUDA
   Toolkit dependency* — and breaks the single-installer story on macOS/Linux.

So: we need a sidecar story that keeps cold start in the milliseconds, ships as
a single binary, and doesn't reopen the distribution wound.

## 2. Options considered

| | Python resident daemon | Go sidecar | TypeScript sidecar | **Rust sidecar (pitched)** |
|---|---|---|---|---|
| Cold start | ~1–5 s first load, ms after (resident) | ms (static binary) | 100s of ms + runtime | ms (static binary) |
| Ships as | 2–4 GB env, CUDA-matched | single binary | needs Node/Bun + native addons | single binary |
| Runs `.pt`-only models | **yes — the only one** | no | no | no (ONNX only) |
| ORT / GPU story | torch-direct, good | cgo → ORT C API (thin, painful) | `onnxruntime-node`, no TRT-RTX worth having | `ort` crate — first-class ORT bindings |
| Fits AGENTS.md dist rules | no | yes | no (new runtime) | **yes** |
| Ecosystem for audio/ML glue | best | thin | thin | good (ndarray, hound, safetensors) |

TypeScript is the weakest option here: it adds a whole JS runtime without
solving GPU execution or `.pt` coverage. Go is a fine *supervisor* (process
management, IPC plumbing) but its ML bindings are cgo wrappers around the same
ORT C API our C# already drives better. Neither runs PyTorch-native models, so
neither removes the export tax — they'd just be a second ONNX frontend.

## 3. Recommendation

**Build the sidecar seam once, fill it with Rust first, keep Python as a
possible second executor behind the same contract.**

- **Phase 1 (this pitch):** a Rust ONNX sidecar (`tools/rust-sidecar-spike/`)
  that owns one job — load an ONNX graph once, serve inference over IPC with
  ms-scale warm calls. It proves the seam: C# stage → IPC contract → sidecar
  process lifecycle (start, health, model-ready, infer, shutdown).
- **Phase 2 (only if needed):** a Python resident daemon implementing the
  *same* IPC contract, used exclusively for `.pt`-only frontier voice models
  (Chatterbox/CosyVoice) where no usable ONNX exists. Same readiness states,
  same artifact story — just a different executor the user opts into.
- **What stays:** C# + in-process ONNX Runtime remains the default local path
  (Whisper, Silero, SortFormer, Kokoro, translation). The sidecar never becomes
  the only way to run anything shippable today.

This preserves everything the product promises: honest readiness
(`process alive` != `model loaded` != `accelerator ready`), resumable stages
with durable artifacts, EP fallback explained truthfully, and no silent new
runtime dependency.

## 4. IPC contract sketch

Keep it boring: JSON-lines over stdin/stdout for the spike (zero dependencies),
graduate to gRPC or named pipes if the seam survives review. The C# side sees
only this (full sketches in `tools/rust-sidecar-spike/`):

```jsonc
// C# → sidecar
{ "id": "req-1", "op": "load",  "model": "models/kokoro-onnx/model.onnx",
  "providers": ["TensorRTRTX", "DirectML", "CPU"] }
{ "id": "req-2", "op": "infer", "inputs": { "input_ids": [101, 234, 999] } }
{ "id": "req-3", "op": "health" }

// sidecar → C#
{ "id": "req-1", "status": "loaded", "activeProvider": "DirectML",
  "loadMs": 1840 }
{ "id": "req-2", "status": "ok", "outputs": { "audio": "<base64 pcm>" },
  "inferMs": 61 }
```

Fallback transparency is the point: the sidecar reports which provider it
*actually* settled on, and C# surfaces that in stage evidence — never "GPU
ready" when the answer was CPU.

## 5. Cold-start story (estimates, to be measured in the spike)

| Cost | Today (spawn python) | Rust sidecar (resident) |
|---|---|---|
| Process start | 1–5 s per call | ~5–20 ms, once |
| Weight load | per call | once; mmap'd safetensors/ONNX |
| CUDA/EP context | per call | once, kept resident |
| Warm infer call | — | IPC + inference only |

The cold hit moves to app start / first-use, where users forgive it — the same
move our ORT engine cache already makes, with the same invalidation discipline
(stale engine → re-verify, same fingerprint rules).

## 6. Open questions for review

1. IPC transport: JSON-lines (spike) vs gRPC vs named pipes — who has a
   preference, and does Avalonia's sandboxing constrain us anywhere?
2. Process supervision: who owns the sidecar lifetime — Composition root, or a
   dedicated `ISidecarHost` behind `Trackdub.Infrastructure`?
3. Signing/notarization: does a second binary complicate the installer signing
   story on macOS/Windows?
4. Do we even need Phase 2 (Python), or does the Rust seam plus continued ONNX
   export work cover the next 12 months of voice models?
5. Benchmark harness: extend `controlled-matrix` with a `--executor sidecar`
   lane so claims come with evidence, per repo policy?

## 7. Decision requested

- [ ] Accept Phase 1 spike (time-boxed, `tools/` only, no `src/` changes)?
- [ ] Accept the two-phase direction (Rust now, Python-behind-same-contract later)?
- [ ] Or reject — and if so, what's the preferred answer to `.pt`-only models?

## Files in this PR (all non-build, decision-only)

- `docs/plans/rust-inference-sidecar-pitch.md` — this document
- `tools/rust-sidecar-spike/Cargo.toml` — dependency sketch (`ort`, `serde`)
- `tools/rust-sidecar-spike/src/main.rs` — load-once / serve-forever loop sketch
- `tools/rust-sidecar-spike/proto/inference.proto` — contract sketch for the
  gRPC graduation path
- `tools/rust-sidecar-spike/csharp/SidecarClient.sketch.cs` — C# caller sketch
  (`.sketch.cs`: deliberately never compiled)

None of this runs yet. That's the point — it's a pitch. Merge means "we agree
this is worth spiking," not "this is the architecture."
