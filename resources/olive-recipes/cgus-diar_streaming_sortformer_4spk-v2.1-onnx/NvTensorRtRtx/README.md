# SortFormer (cgus-diar_streaming_sortformer_4spk-v2.1) — TRT-RTX recipe bundle

Olive recipe for compiling the SortFormer diarization encoder for TensorRT RTX.

## Status

The bundled SortFormer ONNX export (`onnx/model.onnx`) was inspected directly
(onnx 1.22, `load_external_data=False`) against the installed model file: **it
contains zero `com.microsoft::SkipLayerNormalization` and zero
`com.microsoft::BiasGelu` nodes.** It is a plain PyTorch export at opset
`ai.onnx=17` using standard `LayerNormalization` (121 nodes) throughout.

An earlier version of this recipe carried two `GraphSurgeries` pre-passes
that decomposed those `com.microsoft` ops before fp16/mxfp8 conversion, on
the premise that TensorRT RTX v0.3.0 cu12 could not parse them. That premise
does not hold for this model — the ops the passes targeted are not present —
so the passes were removed. They also referenced `ReplaceNodePatternByNode`
and `RemoveIdentityAndCastNodes`, neither of which exists in olive-ai
0.13.0's `Surgeon` registry (`olive/passes/onnx/graph_surgeries.py`), so they
would have failed at run time regardless.

**The actual cause of the `preFlightFailed` / CPU-fallback smoke-gate
failure (requested provider `tensorrt-rtx` != effective provider `cpu`) is
still open.** It is not the `com.microsoft` ops described above. Re-run the
smoke gate against the un-fused model and inspect which op(s) TRT-RTX
actually rejects before adding any new pre-pass here.

This recipe now only applies fp16 (or mxfp8) conversion and TRT-RTX session
param tuning — no graph surgery.

## Usage

```
.\tools\olive\Validate-SortFormerTrtRtx.ps1
```

This runs the encoder recipe, copies the optimized model into
`build/sortformer-4spk-onnx-trtrtx-validated-<precision>/` (`fp16` by default,
`mxfp8` with `-Mxfp8`), and writes `build/sortformer-4spk-trtrtx-validation.json`.
`SortFormerDiarizationEngineTests.cs` has no TRT-RTX smoke test yet — one should
be added (mirroring `WhisperOnnxTrtRtxValidationTests.cs`'s
`[Fact(Skip = "Pending TRT-RTX validation ...")]` pattern) once this recipe is
hardware-validated.

## Staging output

`build/sortformer-4spk-onnx-trtrtx-validated-<precision>/onnx/model.onnx` is
what `SortFormerDiarizationEngine` loads. The C# engine auto-derives the
`benchmark_entry` from the staging directory.
