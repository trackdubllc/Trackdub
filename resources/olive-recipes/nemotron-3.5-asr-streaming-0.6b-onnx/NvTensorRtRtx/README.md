# Nemotron 3.5 ASR Streaming (0.6B) — TRT-RTX recipe bundle

Olive recipes for compiling the Nemotron streaming ASR encoder and decoder_joint
for TensorRT RTX.

## Status

Both bundled exports are plain PyTorch exports at opset `ai.onnx=17`: the encoder
uses standard `LayerNormalization`, and decoder_joint is a 42-node LSTM/MatMul
graph. Neither contains any `com.microsoft::` op, so no graph surgery is needed.
Both load and run on `NvTensorRTRTXExecutionProvider` (EP ABI 0.3.0 cu12) as-is,
verified on an RTX 5070.

The encoder needs a fixed TensorRT shape profile. Without one, TRT-RTX fails to
build the engine: `kOPT values ... violate shape constraints` at
`/encoder/layers.0/self_attn/Reshape_7`. The runtime supplies the profile through
`NemotronAsrEncoderTrtProfiles`:

```
processed_signal:1x128x65,processed_signal_length:1,cache_last_channel:24x1x56x1024,
cache_last_time:24x1x1024x8,cache_last_channel_len:1,prompt_index:1
```

(min = opt = max.) This drives the recipe shapes:

- `encoder_trtrtx_fp16.json`: `OnnxFloatToFloat16` with `keep_io_types: true`
  only. Its `input_model.inference_settings` carries the profile, as
  `nv_profile_*_shapes`, for Olive's own session. It has no
  `OrtSessionParamsTuning` pass because Olive 0.13's tuning baseline session
  overrides `provider_options` with `None`, so the profile cannot reach it and the
  engine build fails. The runtime builds its own session options anyway.
- `decoder_joint_trtrtx_fp16.json`: `OnnxFloatToFloat16` with
  `keep_io_types: true`, plus `OrtSessionParamsTuning` using the
  `nemotron_decoder_joint_step` dummy data config (single RNNT step:
  `encoder_outputs` 1x1024x1, `input_states_*` 2x1x640).

`keep_io_types` keeps graph I/O float32, matching what `NemotronAsrGreedyDecoder`
feeds.

The accelerator entry is `["NvTensorRTRTXExecutionProvider", "${TRT_RTX_EP_PATH}"]`.
Olive registers the EP ABI plugin DLL from that path.

There are no MXFP8 recipes. Olive 0.13's `NVModelOptQuantization` only supports
INT4 weight-only quantization.

Nemotron stays out of ASR auto-planning (`StageRuntimeRequirements`) because of a
separate empty-transcript issue unrelated to TensorRT RTX.

## Usage

```
.\tools\olive\Validate-NemotronAsrTrtRtx.ps1
```

This runs both recipes and stages them in
`build/nemotron-3.5-asr-onnx-trtrtx-validated-fp16/`. It then runs
`trackdub providers trt-rtx verify` against the staged encoder; the smoke path
also loads the decoder_joint beside it and applies the profile above. It records
`pass = true` only if both run on TensorRT RTX, and writes
`build/nemotron-3.5-asr-trtrtx-validation.json`.
