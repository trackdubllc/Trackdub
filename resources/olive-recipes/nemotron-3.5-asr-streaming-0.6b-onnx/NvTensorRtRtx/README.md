# Nemotron 3.5 ASR Streaming (0.6B) — TRT-RTX recipe bundle

Olive recipe for compiling the Nemotron streaming ASR encoder + decoder_joint
for TensorRT RTX.

## Why this exists

The bundled Nemotron ASR encoder contains the same two `com.microsoft::` op
types that defeat TensorRT RTX v0.3.0 cu12's parser:

- `SkipLayerNormalization`
- `BiasGelu`

These appear in the 24-layer FastSpeech-style encoder body. With them
present, TRT-RTX compiles zero nodes and ORT falls back to CPU, which makes
the smoke gate fire `preFlightFailed` because the requested provider
(`tensorrt-rtx`) does not match the effective provider (`cpu`).

The decoder_joint is compiled separately because it has different dynamic
shapes (B × 1 cache state, sequence-by-sequence greedy decoding).

## Fusion strategy

Same as the SortFormer recipe:

1. `OnnxBlockWiseRMSN` — replaces `SkipLayerNormalization` with `LayerNormalization` + `Add`.
2. `OnnxGraphSurgeries` — replaces `BiasGelu` with `Add` + `Gelu`.

After fusion, fp16 conversion and `OrtSessionParamsTuning` produce an
encoder + decoder_joint pair that TRT-RTX can parse and compile.

## Usage

```
.\tools\olive\Validate-NemotronAsrTrtRtx.ps1
```

This runs both encoder and decoder_joint recipes, stages them under
`build/nemotron-3.5-asr-onnx-trtrtx-validated/`, and writes
`build/nemotron-3.5-asr-trtrtx-validation.json`. After that, remove the
`Skip = "Pending TRT-RTX validation"` attribute from
`tests/Trackdub.Inference.Onnx.Tests/NemotronAsrEncoderTrtRtxValidationTests.cs`
when that file is added (see Whisper equivalent for the convention).

## TRT profile shapes

The C# encoder (`NemotronAsrEncoderTrtProfiles.BuildOptions`) declares:

```
processed_signal:1x128x65
processed_signal_length:1
cache_last_channel:24x1x56x1024
cache_last_time:24x1x1024x8
cache_last_channel_len:1
prompt_index:1
```

for `min / opt / max`. If TRT-RTX reports a kMAX self-inconsistency
(`Profile kMAX values are not self-consistent`), the encoder graph has an
additional dynamic dim not covered here — extend the profile string and
re-run. The synthetic eval above feeds the same shapes so latency
measurements are reproducible.
