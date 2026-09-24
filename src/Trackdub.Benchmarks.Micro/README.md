# Trackdub microbenchmarks

This project contains BenchmarkDotNet measurements for small, repeatable
Trackdub operations. It is intentionally separate from the existing
`Trackdub.Benchmarks` controlled pipeline runner, which remains the source of
truth for end-to-end media/model/provider evidence.

## Scope

- Audio resampling
- DeepFilterNet signal processing
- LatentSync tensor preprocessing
- Kokoro and CosyVoice tokenizer encoding
- Opt-in raw single-input ONNX graph execution

The project references only `Trackdub.Inference.Onnx` and BenchmarkDotNet plus
the ONNX Runtime managed package. Model/tokenizer setup belongs in
`GlobalSetup`; measured methods should contain only the operation under test.

## Run locally

```bash
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release
```

With no arguments, the program runs the deterministic CPU subset. List all
benchmarks without running them:

```bash
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- --list flat
```

Use `Release` for measurements. Generated BenchmarkDotNet output is written to
`BenchmarkDotNet.Artifacts/` unless `--artifacts <path>` is supplied.

## Model-backed benchmarks

Tokenizer and ONNX benchmarks are opt-in because they require cached assets:

```bash
TRACKDUB_BDN_KOKORO_MODEL_ROOT=/path/to/Kokoro \
TRACKDUB_BDN_COSYVOICE_MODEL_ROOT=/path/to/CosyVoice \
  dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- \
  --filter "Trackdub.Benchmarks.Micro.*TokenizerBenchmarks.*"
```

```bash
TRACKDUB_BDN_ONNX_MODEL=/path/to/model.onnx \
TRACKDUB_BDN_ONNX_INPUT_SHAPE=1x128x500 \
  dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- \
  --filter "Trackdub.Benchmarks.Micro.OnnxModelBenchmarks.*" \
  --job Short
```

The raw ONNX benchmark supports one float input. `TRACKDUB_BDN_ONNX_INPUT_NAME`
is optional for a one-input graph. Unsupported graph contracts fail clearly in
`GlobalSetup` rather than being measured with invalid input data.

## CI policy

BDN does not run on pull requests. The separate
`.github/workflows/benchmark-dotnet.yml` workflow supports:

- CPU benchmarks manually or on a gated nightly schedule
- Opt-in real-model ONNX benchmarks
- Opt-in comparison against a saved Git commit

For a saved-commit comparison, use
`scripts/ci/run_benchmarkdotnet_baseline.py` as documented in
`docs/benchmarks/benchmarkdotnet.md`. Do not merge BDN timings into
`BenchmarkEvidenceReport`; keep BDN artifacts and controlled pipeline evidence
separate.
