# CosyVoice TTS Adapter

CosyVoice is registered as an experimental voice-cloning TTS family, but it is
not treated as runnable until a full Trackdub-native bundle exists in the model
cache.

Upstream `FunAudioLLM/CosyVoice-300M` does not ship as one Trackdub-ready ONNX
graph. The public weights include `campplus.onnx`, `speech_tokenizer_v1.onnx`,
`flow.decoder.estimator.fp32.onnx`, and Torch `.pt` files for the remaining
runtime pieces. Trackdub cannot use the `.pt` files in the end-user runtime.

## Required Bundle Layout

The adapter expects the downloaded model root to contain:

```text
trackdub/cosyvoice-300m/v1/manifest.json
trackdub/cosyvoice-300m/v1/cosyvoice.yaml
trackdub/cosyvoice-300m/v1/config.json
trackdub/cosyvoice-300m/v1/configuration.json
trackdub/cosyvoice-300m/v1/frontend/campplus.onnx
trackdub/cosyvoice-300m/v1/frontend/speech_tokenizer_v1.onnx
trackdub/cosyvoice-300m/v1/llm/text_encoder.onnx
trackdub/cosyvoice-300m/v1/llm/token_generator.onnx
trackdub/cosyvoice-300m/v1/flow/encoder.onnx
trackdub/cosyvoice-300m/v1/flow/decoder_estimator.onnx
trackdub/cosyvoice-300m/v1/hift/vocoder.onnx
```

The bundled model manifest should pin the Hugging Face revision and provide
SHA-256 hashes for every file before this model is promoted beyond the pending
experimental lane.
