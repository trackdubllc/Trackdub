# Where Trackdub's Windows ML code lives

Tier 3 pointers. This file says where to look, not what is true. Read the file it points to for the current values, especially version pins, which this file deliberately does not copy.

| Concern | Read |
| --- | --- |
| Package pins (Windows ML, GenAI, managed ORT) | `Directory.Packages.props` |
| Per-TFM package wiring (which ORT package is managed-only on Windows) | `src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj` |
| Native asset copies into app output, and shipping the worker to `inference-worker\` | `src/Trackdub.Composition/Trackdub.Composition.csproj` |
| Catalog bootstrap: per-provider ensure and register | `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Which provider uses which registration route; no bulk download on hot paths | `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlProviderRegistrationPolicy.cs` |
| Single catalog EP registration | `src/Trackdub.Inference.Onnx/WinMlCatalog/WindowsMlCatalogEpRegistration.cs` |
| MIGraphX via the catalog | `src/Trackdub.Inference.Onnx/Migraphx/WindowsMlMigraphxCatalogService.cs` |
| TRT-RTX bring-your-own plugin registration | `src/Trackdub.Inference.Onnx/TensorRtRtx/TensorRtRtxPluginService.cs` |
| GenAI native ORT/GenAI pairing verification | `src/Trackdub.Inference.Onnx/Runtime/GenAiNativeRuntimeSelection.cs`, `build/Trackdub.NativePair.targets`, `tools/Trackdub.GenAiProbe/Invoke-Probe.ps1` |
| Per-stage, per-engine provider allow-lists (product decisions) | `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs` |
| Smoke-test guards that refuse a provider before native code runs | `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderSmokeTester.cs` |
| Whether the loaded `onnxruntime.dll` can run DirectML | `src/Trackdub.Inference.Onnx/Runtime/Planning/DirectMlRuntimeProbe.cs` |
| Session-pool VRAM admission factors | `src/Trackdub.Inference.Onnx/Pool/InferenceSessionPool.cs` |
| EP-context artifact capture and validation | `src/Trackdub.Inference.Onnx/EpContext/` |
| ORT 1.30 out-of-process worker (Kokoro on CUDA) | `src/Trackdub.InferenceWorker/`, `src/Trackdub.Inference.Onnx/Worker/` |

Decisions and reference docs:
- `docs/decisions/ADR-0001-winui3-windows-ml.md`, `ADR-0002-windows-ml-provider-strategy.md`, `ADR-0017-ort130-inference-worker.md`
- `docs/reference/windows-ml-stage-provider-matrix.md`, `gpu-execution-providers.md`, `tensorrt-rtx-ep-abi-plugin.md`

Provider exclusions in `StageRuntimeRequirements.cs` are product decisions that rest partly on the observations in [observations.md](observations.md). Their code comments are not platform facts. Before relying on or relaxing one, check the matching observation's status.
