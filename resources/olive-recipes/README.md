# Olive recipes (Trackdub subset)

This folder contains a **trimmed subset** of [microsoft/olive-recipes](https://github.com/microsoft/olive-recipes) recipes used by Trackdub model optimization (Optimize UI, manifest `olive` bindings, and `tools/olive/*` scripts).

Only the recipe trees referenced by `bundled-models.manifest.json`, `Trackdub.App.Avalonia.csproj`, and olive validation scripts are vendored here. The full upstream mirror is not shipped in this repo.

## Bundled recipe trees

| Directory | Used for |
|-----------|----------|
| `openai-whisper-tiny/` | Bundled ASR optimize (CPU/DML) |
| `openai-whisper-base/` | Bundled ASR optimize (CPU/DML) |
| `microsoft-Phi-3.5-mini-instruct/aitk/` | Text refinement optimize |
| `Qwen-Qwen2.5-1.5B-Instruct/` | Translation refine (aitk + NvTensorRtRtx) |
| `onnx-community-whisper-tiny/` | Whisper ONNX TRT-RTX validation |
| `onnx-community-whisper-base/` | Whisper ONNX TRT-RTX validation |
| `onnx-community-whisper-small/` | Whisper ONNX TRT-RTX validation |
| `Xenova-whisper-medium/` | Whisper ONNX TRT-RTX validation |
| `Xenova-whisper-large-v3/` | Whisper ONNX TRT-RTX validation |

## Adding recipes

To add a new olive binding, copy the needed subtree from upstream olive-recipes (or add a git submodule) rather than re-vendoring the full catalog.

Upstream license: see `LICENSE` in this directory.
