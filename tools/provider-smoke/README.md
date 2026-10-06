# Provider smoke harness

Cross-execution-provider smoke tests: EP discovery, session creation, and a
minimal inference run of one ONNX model through each requested provider. This
complements the .NET `Trackdub.Benchmarks` `provider-matrix` scenario — the
matrix measures the shipped pipeline stages end-to-end through the runtime
planner, while this harness answers the narrower question "does provider X
even load, take nodes, and run on this host" in seconds, with no model
provisioning or pipeline setup.

Status semantics (never fabricated):

- `ok` — session created and inference ran; the requested provider is in use.
- `fallback` — session created and inference ran, but all nodes landed on
  `CPUExecutionProvider` (the EP either rejected the graph or has no device).
- `fail` — session creation or inference raised; `detail` carries the error.
- `absent` — platform/provider-library prerequisite missing (expected on the
  wrong host OS or without the accelerator stack).

## Usage

```bash
python tools/provider-smoke/smoke_execution_providers.py \
  --model <model.onnx> \
  --feed <feed.npz> \
  --results smoke.json \
  --providers cpu,openvino
```

- `--feed` is an `.npz` file mapping input names to float32 tensors. It is
  required whenever the model has non-batch dynamic input dims (the harness
  refuses to guess; guessed feeds produce misleading smoke results).
- `--providers` accepts short tokens matched case-insensitively against the
  wheel's available providers: `cpu`, `openvino`, `qnn`, `migraphx`, `dnnl`,
  `coreml`, `cuda`, ... Each entry may carry options after a colon, e.g.
  `openvino:performance_hint=THROUGHPUT`.
- `--warmup-runs` (default 2) and `--repeat-runs` (default 5) control timing;
  `best_ms`/`mean_ms` are recorded per provider when status is `ok` or
  `fallback`.

## Wheel variants

Each ORT EP ships in a separate wheel, and several are mutually exclusive —
run each variant in its own virtual environment:

| Provider | Wheel | Requires |
|---|---|---|
| `cpu` | `onnxruntime` (any variant) | — |
| `openvino` | `onnxruntime-openvino` | OpenVINO runtime (bundled in wheel); CPU plugin works everywhere, NPU needs an Intel NPU |
| `qnn` | `onnxruntime-qnn` | Qualcomm QNN SDK libs (bundled in wheel); HTP backend needs a Snapdragon/Hexagon device |
| `migraphx` | `onnxruntime-migraphx` | AMD ROCm stack (`libmigraphx`) on the host — not bundled |
| `dnnl` | no wheel — build ORT with `--use_dnnl` (see `tools/onnxruntime-dnnl/Build-OnnxRuntimeDnnlNativePackage.ps1`) | the DNNL-enabled native package |
| `coreml` | `onnxruntime-coreml` (macOS universal2 only) | Apple Silicon / macOS |

QNN specifics: the `onnxruntime-qnn` wheel ships the provider library in a
sibling `onnxruntime_qnn` package; the harness registers it via
`ort.register_execution_provider_library` and points `backend_path` at the
bundled `libQnnHtp.so`/`QnnHtp.dll` automatically. Without a Hexagon device
the expected result is `fallback` (CPU takes all nodes) — that is a *passing*
smoke for "EP loads cleanly, device absent," and matches the repo's QNN
readiness-probe semantics. The x86 `QnnCpu` backend is not shipped in the wheel.

## Evidence

Initial sandbox results (Intel Xeon 6985P-C, no GPU/NPU, whisper-base
encoder, 80×3000 features) are posted alongside the CPU-tier spec discussion
(#385) and the tts-bench PR (#388). Summary of status behavior on a
GPU/NPU-less host: `cpu` ok; `openvino` ok on CPU plugin (slower than CPU EP
on transformer encoders; rejects Kokoro's dynamic-rank STFT graph);
`qnn` fallback (clean load, no Hexagon); `migraphx` fallback (missing ROCm
`libmigraphx`); `coreml` absent (not macOS).
