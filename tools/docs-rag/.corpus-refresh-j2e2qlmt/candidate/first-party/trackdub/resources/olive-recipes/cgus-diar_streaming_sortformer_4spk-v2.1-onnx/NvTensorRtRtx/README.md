# SortFormer (cgus-diar_streaming_sortformer_4spk-v2.1) — TRT-RTX recipe bundle

Olive recipe for compiling the SortFormer diarization encoder for TensorRT RTX.

## Status

The bundled export (`onnx/model.onnx`) is a plain PyTorch export at opset
`ai.onnx=17` with standard `LayerNormalization`. It contains no
`com.microsoft::SkipLayerNormalization` or `BiasGelu` nodes, so no graph surgery is
needed. It loads and runs on `NvTensorRTRTXExecutionProvider` (EP ABI 0.3.0 cu12)
as-is, verified on an RTX 5070.

`encoder_trtrtx_fp16.json` applies:

1. `OnnxFloatToFloat16` with `keep_io_types: true`. The graph I/O stays float32
   because `SortFormerDiarizationEngine` feeds float32 tensors.
2. `OrtSessionParamsTuning` on TensorRT RTX, using the `sortformer_steady_state`
   dummy data config (the shapes the engine feeds in steady state: `chunk`
   1x3040x128, `spkcache` 1x188x512, `fifo` 1x40x512: NVIDIA's
   recommended offline config, chunk 340 + right context 40 model frames). The graph has dynamic
   dims, so Olive cannot infer dummy inputs on its own.

The accelerator entry is `["NvTensorRTRTXExecutionProvider", "${TRT_RTX_EP_PATH}"]`.
Olive registers the EP ABI plugin DLL from that path, which requires an onnxruntime
build that has `register_execution_provider_library` / `get_ep_devices`.
`Bootstrap-TrtRtxOliveVenv.ps1` pins one.

There is no MXFP8 recipe. Olive 0.13's `NVModelOptQuantization` only supports INT4
weight-only quantization.

## Usage

```
.\tools\olive\Validate-SortFormerTrtRtx.ps1
```

This runs the recipe, stages the output in
`build/sortformer-4spk-onnx-trtrtx-validated-fp16/`, then runs
`trackdub providers trt-rtx verify` against the staged model. It records
`pass = true` only if the model actually loads and runs on TensorRT RTX, and writes
`build/sortformer-4spk-trtrtx-validation.json`.

## Staging output

`build/sortformer-4spk-onnx-trtrtx-validated-fp16/onnx/model.onnx` is what
`SortFormerDiarizationEngineTests.DiarizeAsync_with_trtrtx_staged_model_selects_tensorrt_rtx_provider`
loads (Skip-gated until run locally).
