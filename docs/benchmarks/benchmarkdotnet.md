# BenchmarkDotNet microbenchmarks

`Trackdub.Benchmarks.Micro` is a separate BenchmarkDotNet project for small,
repeatable CPU and tensor benchmarks. It does not replace the existing
`Trackdub.Benchmarks` controlled pipeline runner or its `BenchmarkEvidenceReport`
artifacts.

## Deterministic CPU benchmarks

Run the default CPU subset in Release mode:

```bash
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release
```

The default filter covers:

- `AudioResamplingBenchmarks`
- `DeepFilterNetSignalBenchmarks`
- `LatentSyncTensorBenchmarks.NormalizeRgba` (the 30-second Whisper-mel variant is available by explicit filter)

To run the deterministic CI subset explicitly:

```bash
dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- \
  --filter "Trackdub.Benchmarks.Micro.AudioResamplingBenchmarks.*" "Trackdub.Benchmarks.Micro.DeepFilterNetSignalBenchmarks.ComputeFeatures*" "Trackdub.Benchmarks.Micro.LatentSyncTensorBenchmarks.NormalizeRgba*" \
  --job Dry \
  --exporters fulljson
```

BenchmarkDotNet writes its normal reports to `BenchmarkDotNet.Artifacts/`.
Use `--artifacts <path>` to select another output directory.

## Tokenizer benchmarks

Tokenizer benchmarks require cached model sidecars and are intentionally not in
the default filter:

```bash
TRACKDUB_BDN_KOKORO_MODEL_ROOT=/path/to/Kokoro \
TRACKDUB_BDN_COSYVOICE_MODEL_ROOT=/path/to/CosyVoice \
  dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- \
  --filter "Trackdub.Benchmarks.Micro.*TokenizerBenchmarks.*"
```

## Opt-in real-model ONNX benchmark

`OnnxModelBenchmarks` measures one raw, single-input float ONNX graph. It is
not part of the default or pull-request benchmark set.

```bash
TRACKDUB_BDN_ONNX_MODEL=/path/to/model.onnx \
TRACKDUB_BDN_ONNX_INPUT_SHAPE=1x128x500 \
  dotnet run --project src/Trackdub.Benchmarks.Micro -c Release -- \
  --filter "Trackdub.Benchmarks.Micro.OnnxModelBenchmarks.*" \
  --job Short
```

`TRACKDUB_BDN_ONNX_INPUT_NAME` is optional when the graph has one input.
Models with multiple inputs or non-float inputs are rejected rather than being
silently measured with an invalid fixture.

## Saved-commit baseline comparison

For a manual comparison against an existing commit, run:

```bash
python3 scripts/ci/run_benchmarkdotnet_baseline.py \
  --baseline-commit <sha> \
  --filter "Trackdub.Benchmarks.Micro.AudioResamplingBenchmarks.*" "Trackdub.Benchmarks.Micro.DeepFilterNetSignalBenchmarks.*" "Trackdub.Benchmarks.Micro.LatentSyncTensorBenchmarks.*" \
  --job Dry \
  --threshold-percent 10
```

The helper runs the same benchmark filter on the current checkout and a
temporary detached worktree at the requested commit, then compares BenchmarkDotNet
mean values by full benchmark name. The comparison is separate from the
controlled pipeline evidence runner.

The GitHub Actions workflow does not run BDN on pull requests. Scheduled CPU runs
are gated by the `TRACKDUB_BENCHMARKDOTNET` repository variable; manual CPU runs,
real-model ONNX benchmarks, and saved-commit comparisons are opt-in.
