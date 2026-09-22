# Handoff: TRT-RTX enablement + runtime efficiency for SortFormer / Nemotron

Repo: `trackdubllc/Trackdub`. Follow-up to PR #247 (branch `Olive`, last commit `c7222ad`).
Combines the Olive recipe enablement work with the CUDA/ORT runtime audit findings into one dependency-ordered plan.

**Anchor doc source: `trackdub-docs-rag` MCP.** This handoff is written to be executed against the hosted first-party docs RAG (`tools/docs-rag/SPEC.md`, endpoint `https://trackdub-docs-rag.trackdub.workers.dev`). Before searching the web, reading vendor PDFs, or reasoning from generic CUDA samples, query the RAG. It is pin-accurate to how Trackdub actually wires providers and to the TRT-RTX EP plugin version this repo ships.

---

## 0. Doc-source policy (read first)

Four complementary sources, in strict preference order for this work:

| Order | Source | Tool | Use for |
|---|---|---|---|
| 1 | **trackdub-docs-rag** (hosted) | `search_trackdub_docs`, `ask_trackdub_docs`, `get_trackdub_doc` | How Trackdub does X: provider wiring, the TRT-RTX pin, Olive recipe conventions, ADR-0002 strategy, manifest/binding rules. First-party (`first-party/**`) outranks vendor on any disagreement. |
| 2 | **onnxruntime scope in RAG** | `search_trackdub_docs` with `scope: "onnxruntime"` | EP options (`enable_cuda_graph`, `nv_profile_*`), IoBinding API, quantization, perf tuning — pinned vendor docs, still inside the RAG. |
| 3 | **trackdub-gpu-docs** (local offline) | `list_corpus`, `search_trackdub_gpu_docs`, `get_doc` | No-network / fast local lookup of the **same** TRT-RTX pin docs. Use when the hosted RAG is unreachable, or for a quick pin sanity check. |
| 4 | NVIDIA CUDA MCP | `search_cuda_docs` | CUDA-internals only (graph replay semantics, pinned memory, launch overhead) that no Trackdub corpus covers. Generic C++/CUDA — not Trackdub-specific, no ORT managed-API coverage. |

Rules that come straight from the SPEC:
- **Scope every RAG query** you can (`trackdub`, `trackdub-gated`, `nvidia`, `onnxruntime`, …). Unscoped `all` is the fallback, not the default.
- **`search` for facts; `ask` is slower and rate-limited** (`ask` limiter: 5 per 10s). Prefer `search`; use `ask` to synthesize across several docs.
- **Vendor `/latest/` pages may be newer than the Trackdub pin — the pin wins.** The RAG's `ask` prompt enforces this; respect it in manual reads too. The offline tool encodes the same precedence: Trackdub local > ORT plugin EP docs > NVIDIA TRT-RTX `/latest/` > WinML catalog docs.
- **On `429`, back off on `Retry-After`.** Do not retry immediately. `limiter: upstream` means AI Search itself is saturated — wait ~10s.

### Offline fallback (`trackdub-gpu-docs`) — concrete usage

Same pin (EP ABI **0.3.0 / cu12**, `tensorrt_rtx_1_5.dll`), keyword search, zero network for repo-local sources (ADR-0002, EP ABI plugin doc, manifest). Setup once:

```powershell
cd tools/mcp-trackdub-gpu-docs
uv sync
uv run trackdub-gpu-docs-ingest   # optional; fetches allowlisted remote URLs. repo-local sources work with zero ingest
```

Query equivalents for the hosted RAG calls used in this handoff:

```jsonc
// Pin sanity + live check vs runtime/trt-rtx-ep.manifest.json  (RAG Phase 0 equivalent)
list_corpus {}

// "how does Trackdub register the TRT-RTX EP / what is the pin"  (RAG scope:"trackdub" equivalent)
search_trackdub_gpu_docs { "query": "TensorRT RTX EP ABI plugin version device name RegisterExecutionProviderLibrary" }

// pull one source in full once search returns its id
get_doc { "id": "<source-id-from-search-hit>" }
```

Mapping when you fall back:

| Hosted RAG call in this handoff | Offline equivalent |
|---|---|
| `search_trackdub_docs { query, scope:"trackdub" }` | `search_trackdub_gpu_docs { query }` (pin/provider scope only) |
| `search_trackdub_docs { …, scope:"nvidia" }` | `search_trackdub_gpu_docs { query }` then filter for NVIDIA TRT-RTX hits |
| `ask_trackdub_docs { query }` | no synthesis offline — `search_trackdub_gpu_docs` + `get_doc`, read and synthesize yourself |
| `get_trackdub_doc { key }` | `get_doc { id }` |

The offline tool does **not** cover the `onnxruntime` / IoBinding / DirectML scopes (that's hosted-RAG only). For those, if the hosted RAG is down, use on-disk `docs/reference/tensorrt-rtx-ep-abi-plugin.md` + `docs/decisions/ADR-0002-windows-ml-provider-strategy.md` and the installed ORT source, and say so in your status.

**The pin this corpus is keyed to (verify, do not assume):** TensorRT-RTX ships as the standalone **ONNX Runtime EP ABI plugin, version 0.3.0, CUDA cu12** (`runtime/trt-rtx-ep.manifest.json`; Windows bundle contains `tensorrt_rtx_1_5.dll`). Device name `NvTensorRTRTXExecutionProvider`, registered via `RegisterExecutionProviderLibrary`. It is **not** the Windows ML catalog EP — the `NvTensorRtRtxExecutionProvider` spelling is the deprecated catalog identity (note the recipe folders are named `NvTensorRtRtx`; that's a path label, not the device name). Confirm: `search_trackdub_docs { query: "TensorRT RTX EP ABI plugin version pin device name", scope: "trackdub" }` or offline `list_corpus {}`.

---

## Goal

Two outcomes on the same two models (`sortformer` diarization, `nemotron-asr`), same EP (TRT-RTX):

- **Track A — Enablement:** make the SortFormer and Nemotron TRT-RTX Olive recipes actually work, then route trt-rtx for those models. The recipes rewrite `com.microsoft::SkipLayerNormalization → Add + LayerNormalization` and `com.microsoft::BiasGelu → Add + Gelu` before fp16/mxfp8 conversion, so TensorRT-RTX can parse the graph instead of falling back to CPU (the smoke gate fires `preFlightFailed` when requested provider `tensorrt-rtx` != effective provider `cpu`).
- **Track B — Runtime efficiency:** four audited findings in the C# inference path. Two only bite once Track A routes these models to TRT-RTX; two are EP-agnostic and can start now.

---

## Verified state (source-checked, not taken on faith)

**Track A state after PR #247:**
- PR #247 was scoped down: manifest, `OliveRecipeResolver.PilotEngineFamilies`, and `ModelManifestTests.cs` were restored to main, so trt-rtx is **not** advertised or routed for sortformer/nemotron-asr. Do not re-enable until Track A step 5.
- Still on the branch as unvalidated tooling: recipe JSONs, READMEs (with a "Known issue" note), `Validate-SortFormerTrtRtx.ps1`, `Validate-NemotronAsrTrtRtx.ps1`, `Bootstrap-TrtRtxOliveVenv.ps1`, `Flip-TrtRtxAsrDiarization.ps1`.
- Recipes use `"type": "GraphSurgeries"` with surgeons `ReplaceNodePatternByNode` and `RemoveIdentityAndCastNodes`. **Neither exists** in olive-ai 0.13.0's Surgeon registry (`olive/passes/onnx/graph_surgeries.py`; keys are lowercased class names). These passes fail at run time. No existing Olive surgeon does this rewrite.
- The original `OnnxBlockWiseRMSN` pass was also wrong: it is an RMSNorm quantization pass, it does not touch SkipLayerNormalization.
- Correct Olive pass type is `GraphSurgeries`; surgery entries are keyed `"surgeon"` (not `surgeon_type`). Working reference: `resources/olive-recipes/google-bert-bert-base-multilingual-cased/aitk/bert-base-multilingual-cased_trtrtx.json`.
- The claim that the models actually contain these ops came from the PR README and has **not** been verified against the real graphs.

**Track B findings (each cited claim re-checked against source this session):**
- **B1 — `enable_cuda_graph=1` is set unconditionally.** Confirmed: `OnnxExecutionSessionFactory.BuildTensorRtRtxOptions` hardcodes `["enable_cuda_graph"] = "1"` for every TRT-RTX session, no gate, no opt-out.
- **B2 — encoder cache round-trips through host every chunk.** Confirmed: `NemotronAsrGreedyDecoder.cs:50-53` clones `cache_last_channel_next` (24×1×56×1024 ≈ 5.25 MB) + `cache_last_time_next` back to host and re-feeds them next iteration. `CloneTensor` (bottom of same file) uses `foreach (T value in tensor) data[index++] = value` — enumerator per element, not `Buffer.BlockCopy`. No `IoBinding` exists anywhere in `src/` (grep: zero matches).
- **B3 — decoder is launch-bound.** Confirmed: `MaxSymbolsPerStep = 10`, one `RunWithRetry` per symbol per frame; `featureExtractor.BuildChunk` runs synchronously inside the chunk loop, so CPU mel extraction for chunk N+1 never overlaps GPU work for chunk N.
- **B4 — retry can't recover CUDA errors.** Confirmed and upgraded from "worth a look" to a real finding: `InferenceRetryPolicy.RunWithRetry` just calls `session.Run(inputs)` again on the **same session**, no recreation. A sticky CUDA/TRT-RTX context error survives a bare re-run, so the retry's own doc-comment ("GPU memory pressure … on DML/TRT-RTX") overstates what it can do for the CUDA path. (Session-creation-time OOM *is* handled correctly elsewhere: `DeviceFallbackSessionCreator` + `DeviceOomExceptionHelper` exclude the device and fall back. The gap is mid-inference retry on a live session.)

**Sequencing fact (source-verified this session):** `OliveRecipeResolver.PilotEngineFamilies` contains only `whisper-genai`, `whisper-onnx`, `phi-genai`. `sortformer` and `nemotron-asr` are absent → `Resolve` returns `AutoOpt` and bypasses recipe bindings. **So B1 and B2 are latent for these models until Track A step 5 adds them to that set.** B3 and B4 are EP-agnostic and independent.

---

## How the two tracks relate

```
Track A (enablement)              Track B (runtime efficiency)
  offline Olive recipe             runtime C# inference
  export-time graph rewrite        OnnxExecutionSessionFactory / NemotronAsrGreedyDecoder
  "does the graph parse            "is the session fast + correct
   on trt-rtx?"                     once it parses?"
        |                                   |
        |   B1, B2 only matter once  -------+  (blocked-by Track A for Nemotron)
        |   the model routes to TRT-RTX     |
        |                                   |
        +---- B3 (mel pipeline), B4 (retry) +  EP-agnostic - start now
```

Three real connection points:
1. **Two silent EP degradations, same gate.** Track A's parse-gate ("effective provider is trt-rtx, not cpu") is necessary but **not sufficient** — B1's CUDA-graph capture against unstable addresses can pass the parse gate yet still replay wrong or slow. Track A step 2.4 must also confirm the graph actually captures (or land with the B1 gate flipping `enable_cuda_graph` off first).
2. **Shared ONNX inspection.** Track A step 1 inspects the real encoder graph (op types, epsilon, bias inputs) — the same graph whose I/O B2's IoBinding needs. Capture input/output tensor names + shapes once, reuse for both.
3. **Doc governance.** The `nvidia-cuda-docs` MCP keep-or-drop question (cubic raised it) is decided by whether these recipes ship. Its highest-value use here is exactly the mxfp8/fp16 precision work. Per the SPEC it's now explicitly the **last** fallback, behind both `trackdub-docs-rag` and the local `trackdub-gpu-docs`; if the recipes don't ship, the argument to drop it strengthens. (SortFormer has no decode loop, so of the runtime findings only B1 applies to it.)

---

## Files involved

**Track A:**
- `resources/olive-recipes/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/NvTensorRtRtx/encoder_trtrtx_{fp16,mxfp8}.json`, `README.md`
- `resources/olive-recipes/nemotron-3.5-asr-streaming-0.6b-onnx/NvTensorRtRtx/{encoder,decoder_joint}_trtrtx_{fp16,mxfp8}.json`, `README.md`
- `tools/olive/Validate-SortFormerTrtRtx.ps1`, `Validate-NemotronAsrTrtRtx.ps1`, `Bootstrap-TrtRtxOliveVenv.ps1`, `Flip-TrtRtxAsrDiarization.ps1`
- `src/Trackdub.Application/ModelOptimization/OliveRecipeResolver.cs` (`PilotEngineFamilies`, `Resolve`)
- `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`
- Model inputs: `models/sortformer/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/onnx/model.onnx`, `models/tonythethompson/nemotron-3.5-asr-streaming-0.6b-onnx/{encoder,decoder_joint}.onnx`

**Track B:**
- `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs` (`BuildTensorRtRtxOptions`, B1)
- `src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrGreedyDecoder.cs` (`CloneTensor`, decode loop, B2/B3)
- `src/Trackdub.Inference.Onnx/NemotronAsr/NemotronAsrEncoderTrtProfiles.cs` (pinned shapes, B1 precondition)
- `src/Trackdub.Inference.Onnx/Pool/InferenceRetryPolicy.cs` (B4)

---

## Phase 0 — Gate (before any code)

1. **RAG reachability + pin sanity.** `search_trackdub_docs { query: "TensorRT RTX EP plugin pin version provider registration", scope: "trackdub" }`. Confirm the endpoint answers and the pin matches section 0. If the hosted RAG is down, use the offline tool: `list_corpus {}` (live pin check vs the manifest) + `search_trackdub_gpu_docs { query: "TRT-RTX EP ABI plugin pin" }`, and note in your status that you're on the offline path. Do not proceed on guessed provider facts.
2. **Hardware + models.** Confirm GPU + TRT-RTX (`NvTensorRTRTXExecutionProvider`) and the model files are present. If not, say so and **stop** — do not ship an unverified rewrite.

---

## Phase 1 — EP-agnostic wins (no hardware, start now)

These do not depend on Track A and help whether Nemotron runs on CPU, DML, or TRT-RTX.

**B3a — Pipeline mel extraction off the decode loop.** In `NemotronAsrGreedyDecoder.Decode`, `featureExtractor.BuildChunk` for chunk N+1 currently runs synchronously before chunk N's GPU work. Overlap them (produce next chunk's features while the current encoder/decoder runs). Cheapest real win; pure CPU-side restructuring.

**B4 — Fix retry semantics.** `InferenceRetryPolicy.RunWithRetry` re-runs the same session, ineffective against sticky CUDA errors. Decide one of:
- Narrow the doc-comment + `IsTransient` to what a bare re-run can actually recover (transient DML hiccups), and route CUDA/TRT-RTX OOM to the existing device-fallback path instead of silent 3x re-throw; or
- Have retry recreate/reset the session for the CUDA path (heavier; confirm against RAG what the session lifecycle contract is: `search_trackdub_docs { query: "session recreation device fallback OOM exclusion", scope: "trackdub" }`).

Do not change `DeviceFallbackSessionCreator` behavior — it's correct.

---

## Phase 2 — Track A: make the recipes real (the bulk of the work)

**Verify every Olive/onnxscript API against installed source before using it. A plausible-sounding API name was wrong twice on PR #247.** Also check the RAG first for Trackdub's own recipe conventions: `search_trackdub_docs { query: "Olive recipe GraphSurgeries convention trt-rtx pre-step", scope: "trackdub" }`.

**A1 — Inspect the real ONNX graphs (onnx Python).** List op types; count `SkipLayerNormalization` / `BiasGelu` nodes; record each node's `epsilon`, whether the optional 5th `bias` input is present, which outputs are consumed, and the model's opset imports. **Report before writing any rewrite.** If the ops are absent, **stop and report** — the recipes' premise is wrong. Also check (RAG `scope: "nvidia"` / `scope: "onnxruntime"`; offline `search_trackdub_gpu_docs` for the NVIDIA TRT-RTX pin; `search_cuda_docs` only if still unresolved) whether TRT-RTX already handles the `com.microsoft` ops — if so the rewrite may be unnecessary and the CPU fallback has another cause. **Capture the encoder input/output tensor names + shapes here — B2 needs them.**

**A2 — Write the rewrite as a standalone pre-step** (onnx or `onnxscript.rewriter`) under `tools/olive/`, run before `olive run`. Do not guess Olive's custom-pass plugin API; if a custom pass is wanted, verify the registration mechanism against installed source and a working example first.
- `SkipLayerNormalization`: fold the optional bias into the `Add`; carry `epsilon` onto `LayerNormalization` (ORT contrib default `1e-12`, ONNX `LayerNormalization` default `1e-5`); wire consumed extra outputs `mean`, `inv_std_var`, and the 4th output `input_skip_bias_sum`. Handle both 4-input and 5-input forms.
- `BiasGelu`: `Add` + `Gelu`. `Gelu` is opset 20, `LayerNormalization` is opset 17 — raise the model's opset import if required, or decompose `Gelu` via `Erf`.

**A3 — Clean up the fiction.** Remove the non-existent surgeons from all six recipe JSONs and the "Known issue" note from both READMEs once the real pre-step exists. Update `Bootstrap-TrtRtxOliveVenv.ps1` to install any new dep (onnx / onnxscript). Have the validators invoke the pre-step before `olive run`.

**A4 — Verification (most of the work):**
- Assert no `com.microsoft::SkipLayerNormalization` / `BiasGelu` nodes remain.
- Run original vs rewritten graph on CPU with **identical, realistic** inputs (not only random); compare within tolerance.
- Run the rewritten graph on `NvTensorRTRTXExecutionProvider`; confirm effective provider is trt-rtx, not cpu. **Also confirm the CUDA graph actually captures** (this is the B1 link — see Phase 3).
- Replace the validators' `pass = output file exists` logic with a check that performs the two verifications above (`Flip-TrtRtxAsrDiarization.ps1` trusts that flag).
- Add a post-optimization check that no `SkipLayerNormalization` / `BiasGelu` nodes remain in the staged model.

**A5 — Route it (only after A4 passes on hardware).** Run `Flip-TrtRtxAsrDiarization.ps1` and commit. Flip does **not** add `sortformer` / `nemotron-asr` to `OliveRecipeResolver.PilotEngineFamilies` — without that, `Resolve` returns `AutoOpt` and bypasses the bindings. Add those two families to `PilotEngineFamilies` (in Flip or by hand) and add resolver coverage. Restore the manifest tests: SortFormer trt-rtx binding assertion, and Nemotron **encoder + decoder_joint** binding assertions.

---

## Phase 3 — Track B runtime findings that Track A unblocks (Nemotron on TRT-RTX)

Do these once A5 routes Nemotron to TRT-RTX. Order: **B2 before B1** — IoBinding is the prerequisite that makes CUDA graphs legitimate.

**B2 — IoBinding for the encoder cache ping-pong (biggest win, real refactor).** `cache_last_channel_next` (output) feeds straight into `cache_last_channel` (input) next iteration and never needs to leave the GPU. With `IoBinding` (none exists yet — this is a new pattern in `Trackdub.Inference.Onnx`), ping-pong two device buffers and skip the host transfer + the per-element `CloneTensor` enumerator entirely. Use the tensor names/shapes captured in A1. Confirm the API shape against the pinned ORT docs: `search_trackdub_docs { query: "ONNX Runtime IoBinding device buffer bind input output", scope: "onnxruntime" }` (hosted only — the offline tool lacks the onnxruntime scope; fall back to installed ORT source + `docs/reference` if the hosted RAG is down). Pinned-memory rationale via `search_cuda_docs` only if needed.

**B1 — Gate `enable_cuda_graph`.** Today it's on for every TRT-RTX session. CUDA graph replay reuses captured memory addresses; the current decode loop allocates fresh input tensors every step, so replay is capturing/replaying against unstable addresses (shapes are fine — `NemotronAsrEncoderTrtProfiles` pins min==max==opt; only addresses move). Two acceptable outcomes:
- If B2 lands first and buffers are now stable device allocations, `enable_cuda_graph=1` becomes legitimate — verify capture actually happens and measure the win.
- Otherwise, gate the flag (per-model / per-session opt-in) so it's not silently wasting work or risking stale-address replay on models that don't meet the precondition.

Measure both ways; confirm graph-update/replay semantics via `search_cuda_docs { query: "CUDA graph update node parameters memory addresses replay" }` (genuine CUDA-internals — no Trackdub corpus will have it).

---

## Other open items to close (from PR #247 review)

- **Duplicate Nemotron manifest bindings.** Encoder and decoder_joint bindings both match `trt-rtx` + `fp16`, and `OliveRecipeResolver.Resolve` takes `FirstOrDefault`, so decoder_joint is never selected. Fix with either a `component` key on `ModelOptimizationRecipeBinding` + resolver change, or one recipe per model covering both components. (Ties into A5 test restoration.)
- **Validator `pass` flag is file-existence only** (Copilot comments on both validators); Flip trusts it. Covered by A4.
- **Epsilon preservation + optional 5-input bias variant** (cubic comments). Covered by A2.
- **Validators hardcode `$RepoRoot\models\...`** and ignore `TRACKDUB_MODEL_CACHE` (all three, incl. Whisper). Decide whether to support the env var.
- **Shared-helper refactor** left out of scope on #247: extract shared bootstrap/recipe-resolve/staging helper across the three validators; parameterized staging helper for encoder/decoder in the Nemotron validator.
- **`nvidia-cuda-docs` MCP governance.** cubic flagged committing a third-party NVIDIA MCP for every agent. Per `docs-rag/SPEC.md` it is the last fallback behind `trackdub-docs-rag` and local `trackdub-gpu-docs`. Owner decides keep-or-drop; leans on whether these recipes ship. Not a code bug.
- **`SortFormerDiarizationEngineTests.cs`** has no TRT-RTX smoke test; add one mirroring `WhisperOnnxTrtRtxValidationTests.cs` (`[Fact(Skip = "Pending TRT-RTX validation …")]`) once validated.
- **CodeFactor** was failing on PR #247 throughout; no details via `gh`. Check its dashboard.

---

## Rules

- **Anchor on `trackdub-docs-rag`.** First-party docs answer "how does Trackdub do X." Scope your queries; `search` over `ask`; back off on `429`. Local `trackdub-gpu-docs` is the offline mirror for the TRT-RTX pin; NVIDIA CUDA MCP is CUDA-internals last resort.
- Verify every Olive/onnxscript API against installed source before using it.
- Do not mark PR review threads resolved unless the behavior is actually verified.
- Follow repo instructions (CLAUDE.md / AGENTS.md): Serena symbolic tools for C# where available; commit only when asked; stage files by name (`git commit -m`, no interactive editor).
- If hardware or models are unavailable, say so and stop.
- Never fake readiness: provider registered != graph parses on trt-rtx != CUDA graph captures != stage succeeded. Keep those distinct in every status you report.

---

## Suggested Linear shape

Linear issue creation was blocked by the workspace free-tier limit on the docs-rag rollout; reference this handoff. When capacity allows, model as one epic + linked issues (`repo:core`, `area:inference`):
- **Epic:** TRT-RTX Olive enablement — SortFormer + Nemotron.
- **A (blocking, hardware-gated):** the Phase 2 recipe work.
- **B3a, B4 (independent, start now):** mel pipelining, retry semantics.
- **B2, B1 (blocked-by A5 for Nemotron):** IoBinding cache ping-pong, then CUDA-graph gate. Note B1 also applies to SortFormer (no decode loop, so B2/B3 do not).
