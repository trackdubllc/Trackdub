# GPU execution providers

How Trackdub selects ONNX Runtime execution providers, what each build can run,
and how to verify GPU execution end to end.

## Builds and provider reach

`Trackdub.Cli`, `Trackdub.Benchmarks`, and `Trackdub.Inference.Onnx` multi-target
on Windows:

| Target framework | Providers that can actually create sessions |
|------------------|---------------------------------------------|
| `net10.0` (portable, every OS) | CPU, **TensorRT RTX** (EP ABI plugin, Windows/Linux NVIDIA) |
| `net10.0-windows10.0.19041.0` | CPU, TensorRT RTX, **DirectML**, Windows ML catalog EPs (MIGraphX, OpenVINO catalog, QNN, VitisAI) |
| Linux `net10.0` | CPU, TensorRT RTX, native CUDA/TensorRT (system ORT GPU build) |

`OnnxRuntimeBuildCapabilities` encodes this per target framework. Provider
discovery consults it, so `trackdub providers list` on the portable build reports
DirectML and the Windows ML catalog EPs as unavailable instead of listing them
as selectable. Requesting an unrunnable provider prints a CLI warning naming the
required target; the request then falls through to the next provider.

## Provider order

On Windows with an NVIDIA GPU the effective preference chain is:

```text
TensorRT RTX (EP ABI plugin) → DirectML → CPU
```

More precisely, `Milestone5PlanningPolicy.SupportedProvidersThisMilestone` orders
all providers (`TensorRTRtx → Migraphx → OpenVinoCatalog → Qnn → VitisAi →
TensorRt → Cuda → OpenVino → DirectMl → Dnnl → Cpu`), and each stage intersects
that with its own allow-list. The planner picks the first provider that is
discovered **and** passes the per-model smoke test; smoke failure falls through
to the next provider. Provider registration alone never counts as readiness.

## CLI controls

| Flag | Behavior |
|------|----------|
| `--prefer-gpu` | Soft preference: vendor EP first (Windows NVIDIA → trt-rtx, AMD → migraphx, Intel → openvino-catalog; Linux NVIDIA → cuda), then DirectML, then CPU. |
| `--require-gpu` | Same resolution but hard-requires the preferred EP where the stage allows it. |
| `--execution-provider <kind>` | Soft pin to a specific provider; falls through on family exclusion or smoke failure. On Windows, `cuda`/`tensorrt` are accepted as compatibility aliases for `trt-rtx`. |
| `--require-execution-provider` | Hard pin: no fallthrough when the stage allows the provider. |

## Engine-family exclusions

Some engine families can never see TensorRT providers because the combination is
broken at the native level, including failures that **kill the host process**
rather than throwing a catchable error. `StageRuntimeRequirementsCatalog` encodes
these as `AllowedProvidersByEngineFamily` overrides:

| Stage | Family | Reason |
|-------|--------|--------|
| ASR | `whisper-onnx` | Olive exports use contrib ops TRT RTX cannot import |
| ASR | `whisper-genai` | ORT GenAI `NvTensorRtRtx` can terminate the process |
| Translation | `opus-mt`, `madlad` | InferenceSession ctor stack overflow under TRT RTX |
| Translation | `phi-genai` | Same GenAI crash class as `whisper-genai` |
| TextRefinement | `qwen-instruct`, `phi-genai` | Same GenAI crash class (observed on Qwen2.5-1.5B) |
| TTS | `kokoro` | CPU-only (ConvTranspose block) |
| TTS | `chatterbox`, `cosyvoice`, `qwen3-tts` | Contrib ops / graphs TRT RTX cannot import |
| LipSynthesis | `latentsync-diffusion` | `MultiHeadAttention` TRT RTX cannot import |

Because a fatal crash cannot be caught and reported as a smoke failure, the
smoke tester also **refuses** the combinations outright before touching native
code: `ThrowIfGenAiTensorRtProvider` for ORT GenAI model loads, and
`ThrowIfFatalTensorRtFamily` for `opus-mt` / `madlad` encoder-decoder session
construction. The smoke sweep bypasses stage allow-lists by design, so these
guards live at the point of danger; affected targets report `FAIL` in
`trackdub providers trt-rtx smoke` rather than attempting the load.

## Verifying GPU execution

```powershell
# Readiness probe (no session):
trackdub providers trt-rtx status

# Honest per-provider availability for the current build:
trackdub providers list

# Planner-style smoke across cached bundled models (incremental PASS/FAIL/SKIP
# lines; results are emitted per target so a mid-catalog crash loses nothing):
trackdub providers trt-rtx smoke
```

For real pipeline evidence, run a stage and check the logged selected provider,
not the readiness output. Registration ≠ model loaded ≠ ran ≠ succeeded.

## References

- [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md): plugin bundle, install, license, smoke
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md): per-stage allow-lists and smoke history
- [ADR-0002](../decisions/ADR-0002-windows-ml-provider-strategy.md): provider strategy
