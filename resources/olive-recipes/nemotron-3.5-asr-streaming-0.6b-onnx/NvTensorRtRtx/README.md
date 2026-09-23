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

Nemotron stays out of ASR auto-planning (`StageRuntimeRequirements`) — not a
TensorRT RTX issue. The blank_id/normalize/RNNT-SOS decode bugs and the
IoBinding output-pairing crash from the original integration are fixed, and the
mel extraction now matches NeMo/parakeet-rs exactly (frame count, STFT window
centering). Decoding is correct. Accuracy is still not competitive with
qwen3-asr-0.6b on real clips:

| Clip | Nemotron | qwen3-asr-0.6b |
|---|---|---|
| marge (EN) | 2/5 lines, dropped words | 5/5 |
| tut (EN) | empty, fell back to qwen | 2/2 |
| tension (EN) | 1 garbled line | 3 lines |
| aura (DE) | empty, fell back to qwen | 3 lines |
| russia (RU) | 5/11 lines, incl. a Japanese hallucination | 11/11, accurate |
| reflexion (ES) | 80/154 lines, many errors | 154/154, accurate |

Root cause: the cache-aware streaming encoder resets `cache_last_channel` /
`cache_last_time` to zero at the start of every VAD region, so it has no prior
audio context and loses roughly the first second of each region. Most speech
regions in real content are short, so this hits hard. Padding a region with
lead-in silence recovers some words but is fragile (too much padding zeros the
output entirely) and isn't a fix. A real fix needs cross-region cache
carry-over (feed the previous region's trailing cache instead of resetting) or
merging adjacent short regions before ASR — neither is implemented.

Nemotron is also 4–7x slower per clip than qwen3-asr-0.6b even after the #253
prefetch/IoBinding speedups (110–210s vs 15–46s on these clips, most of it
TensorRT RTX engine build/session warmup on each new process).

Explicit `--model nemotron-3.5-asr` overrides still work; auto-planning keeps
picking qwen3-asr-0.6b.

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
