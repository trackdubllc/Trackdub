# Nemotron 3.5 ASR Streaming (0.6B) — TRT-RTX recipe bundle

Olive recipe for compiling the Nemotron streaming ASR encoder + decoder_joint
for TensorRT RTX.

## Status

Both bundled ONNX exports (`encoder.onnx`, `decoder_joint.onnx`) were
inspected directly (onnx 1.22, `load_external_data=False`) against the
installed model files: **neither contains any `com.microsoft::` op.** The
encoder is a plain PyTorch export at opset `ai.onnx=17` using standard
`LayerNormalization` (144 nodes); the decoder_joint is a 42-node LSTM/MatMul
graph, also plain `ai.onnx=17`.

An earlier version of this recipe pair carried two `GraphSurgeries`
pre-passes per model that decomposed `SkipLayerNormalization` and
`BiasGelu` before fp16/mxfp8 conversion, on the premise that TensorRT RTX
v0.3.0 cu12 could not parse them. That premise does not hold for either
model — the ops the passes targeted are not present — so the passes were
removed. They also referenced `ReplaceNodePatternByNode` and
`RemoveIdentityAndCastNodes`, neither of which exists in olive-ai 0.13.0's
`Surgeon` registry (`olive/passes/onnx/graph_surgeries.py`), so they would
have failed at run time regardless.

**The actual cause of the `preFlightFailed` / CPU-fallback smoke-gate
failure (requested provider `tensorrt-rtx` != effective provider `cpu`) is
still open.** It is not the `com.microsoft` ops described above. Re-run the
smoke gate against the un-fused models and inspect which op(s) TRT-RTX
actually rejects before adding any new pre-pass here.

The decoder_joint is compiled separately because it has different dynamic
shapes (B × 1 cache state, sequence-by-sequence greedy decoding).

Each recipe now only applies fp16 (or mxfp8) conversion and TRT-RTX session
param tuning — no graph surgery.

## Usage

```
.\tools\olive\Validate-NemotronAsrTrtRtx.ps1
```

This runs both encoder and decoder_joint recipes, stages them under
`build/nemotron-3.5-asr-onnx-trtrtx-validated-<precision>/` (`fp16` by default, `mxfp8` with `-Mxfp8`), and writes
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
