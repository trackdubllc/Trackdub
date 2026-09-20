# SortFormer (cgus-diar_streaming_sortformer_4spk-v2.1) — TRT-RTX recipe bundle

Olive recipe for compiling the SortFormer diarization encoder for TensorRT RTX.

## Why this exists

The bundled SortFormer ONNX export contains two op types that TensorRT RTX
v0.3.0 cu12 cannot parse:

- `com.microsoft::SkipLayerNormalization`
- `com.microsoft::BiasGelu`

Both come from the FastSpeech-style Conformer blocks and appear in every
encoder layer (17+ layers). Until the model is re-exported with these ops
fused to their TRT-RTX-compatible equivalents (`LayerNormalization + Add`
and `Gelu + Add`), TRT-RTX falls through to CPU and the smoke gate fires
`preFlightFailed` because the requested provider (`tensorrt-rtx`) does not
match the effective provider (`cpu`).

This recipe applies two pre-fusion passes **before** fp16 conversion so the
TRT-RTX parser sees the standard ops it knows how to compile:

1. `OnnxGraphSurgeries` (ReplaceNodePatternByNode) — decomposes `SkipLayerNormalization` into `Add` + `LayerNormalization`.
2. `OnnxGraphSurgeries` (ReplaceNodePatternByNode) — decomposes `BiasGelu` into `Add` + `Gelu`.

## Usage

```
.\tools\olive\Validate-SortFormerTrtRtx.ps1
```

This runs the encoder recipe, copies the optimized model into
`build/sortformer-4spk-onnx-trtrtx-validated/onnx/model.onnx`, and writes
`build/sortformer-4spk-trtrtx-validation.json`. After that, remove the
`Skip = "Pending TRT-RTX validation"` attribute from
`tests/Trackdub.Inference.Tests/SortFormerDiarizationEngineTests.cs` (the
test scaffolding already exists; see the Whisper equivalent for the
convention).

## Staging output

`build/sortformer-4spk-onnx-trtrtx-validated/onnx/model.onnx` is what
`SortFormerDiarizationEngine` loads. The C# engine auto-derives the
`benchmark_entry` from the staging directory.
