# Trackdub.OnnxRuntime.Dnnl.Native

Trackdub-owned native package for ONNX Runtime builds with the oneDNN/DNNL execution provider enabled.

Canonical manifest runtime token: `onnxruntime-dnnl`.

Initial supported RIDs:

- `win-x64`
- `linux-x64`
- `osx-x64`

Native binaries are intentionally not checked in. Build from the ONNX Runtime tag matching `Microsoft.ML.OnnxRuntime`, then place native assets under `runtimes/<rid>/native/` and provenance under `provenance/`.
