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

So: we need a sidecar story that keeps warm calls in the milliseconds, ships as
a small native binary with colocated provider libs (not a language runtime),
and doesn't reopen the distribution wound.

## 2. Options considered

| | Python resident daemon | Go sidecar | TypeScript sidecar | **Rust sidecar (pitched)** |
|---|---|---|---|---|
| Cold start | ~1–5 s first load, ms after (resident) | ms (static binary) | 100s of ms + runtime | ms process start; first inference unmeasured (spike measures) |
| Ships as | 2–4 GB env, CUDA-matched | single binary | needs Node/Bun + native addons | small binary + colocated ORT/provider libs (GPU EPs ship as shared libs, not static) |
| Runs `.pt`-only models | **yes — the only one** | no | no | no (ONNX only) |
| ORT / GPU story | torch-direct, good | cgo → ORT C API (thin, painful) | `onnxruntime-node`, no TRT-RTX worth having | `ort` crate — first-class ORT bindings |
| ORT copies / version skew | n/a (torch world) | second ORT via cgo | second ORT via node addon | **second ORT copy** — must pin the same ORT version as the C# app or engine/EP-context caches diverge (see Q6) |
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
  whose value is the seam itself, stated plainly: it proves the out-of-process
  contract Phase 2 needs (C# stage → IPC contract → sidecar lifecycle: start,
  health, model-ready, infer, shutdown) and measures its tax. It does NOT run
  `.pt`-only models and does NOT beat in-process ORT on raw latency — ONNX
  models already run resident in-process, so Phase 1's benchmark is
  sidecar-vs-in-process on the same graph, and success means IPC overhead
  within budget, not a speedup.
- **Phase 2 (NOT proposed here — needs its own governance decision):** a Python
  resident daemon implementing the *same* IPC contract, used exclusively for
  `.pt`-only frontier voice models where no usable ONNX exists. AGENTS.md
  forbids end-user Python/Conda/Docker/CUDA-Toolkit dependencies and opt-in
  does not lift that rule, so Phase 2 is blocked on a governance change plus a
  defined experimental packaging boundary (cf. strategy.md) — this pitch asks
  only for the Rust spike, which keeps Phase 2 possible without pre-approving it.
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
{ "id": "req-1", "op": "load", "plan": { "model": "models/kokoro-onnx/model.onnx",
  "providers": ["TensorRTRTX", "DirectML", "CPU"], "requirePreferred": false } }
{ "id": "req-2", "op": "infer", "inputs": { "input_ids":
  { "dtype": "int64", "shape": [1, 3], "data": "<base64>" } } }
{ "id": "req-3", "op": "health" }

// sidecar → C#
{ "id": "req-1", "status": "loaded", "activeProvider": "DirectML",
  "loadMs": 1840 }
{ "id": "req-2", "status": "ok", "outputs": { "audio": "<base64 pcm>" },
  "inferMs": 61 }
```

Tensors are typed on the wire (`{ dtype, shape, data }` with base64 payload)
— the same envelope as the proto sketch's `TensorMeta`, so the spike exercises
the real contract and the gRPC graduation is a transport swap, not a redesign.
Base64 costs ~33% on payloads (notably PCM outputs); the spike measures it
before we commit to a transport.

The `load` payload is a planner-approved plan (integrity-qualified path plus
the planner's ordered provider fallback and hard-pin flag), mirroring
`StageRuntimePlan` — the sidecar executes it and never selects models or
providers itself, so `IRuntimePlanner` gates and
`RequirePreferredExecutionProvider` cannot be bypassed or silently violated.

Fallback transparency is the point: the sidecar reports which provider it
*actually* settled on, and C# surfaces that in stage evidence — never "GPU
ready" when the answer was CPU.

## 5. Cold-start story (estimates, to be measured in the spike)

Terminology, kept honest: "process start" (ms-scale for native binaries) is NOT
"first inference" — weight load plus EP/CUDA init dominates first use (the
`loadMs: 1840` example is illustrative, and the real number is an explicit
spike measurement, not a decision criterion).

| Cost | In-process ORT today (Phase 1 baseline) | Rust sidecar (resident) | Spawn-python per call (Phase 2 motivation) |
|---|---|---|---|
| Process start | none (in-process) | ~5–20 ms, once | 1–5 s per call |
| Weight load | once per session pool | once; mmap'd ONNX | per call |
| EP/CUDA context | once per session pool | once, kept resident | per call |
| Warm infer call | inference only | inference + IPC + base64 tax | — |

Two different stories, kept separate: Phase 1 is expected to be *slightly slower*
than in-process ORT on warm calls (IPC + base64) — its deliverable is the proven
seam plus a measured overhead number. The cold-start win belongs to Phase 2,
where a resident Python collapses per-call interpreter/torch/CUDA costs into a
one-time warmup at app start / first use — the same move our ORT engine cache
already makes, with the same invalidation discipline (stale engine → re-verify,
same fingerprint rules).

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
6. Distribution + ORT pinning: the sidecar ships its own ORT copy plus GPU
   provider shared libs alongside the C# app's copy. Who owns the version pin
   across both processes, and how do engine/EP-context fingerprints stay valid
   when either side bumps? (A second binary also widens the signing story in Q3.)

## 7. Decision requested

- [ ] First: is the `.pt`-only problem (Q4) real enough to need the seam at all —
  or does continued ONNX export work cover the next 12 months of voice models?
- [ ] Accept Phase 1 spike (time-boxed, `tools/` only, no `src/` changes),
  judged on seam-proven + IPC-overhead-measured, not on speedup?
- [ ] Accept the narrowed direction (Rust spike now; Phase 2 deferred to a
  separate governance + packaging decision, not pre-approved here)?
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
