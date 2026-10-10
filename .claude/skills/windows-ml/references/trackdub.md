# Windows ML in Trackdub

These are facts about this repository, as of branch `feat/winml-2.4` on 2026-10-09. Source and tests win over this file. If a path below has moved, find it with symbol search before trusting the rest.

## Pins

All pins live in `Directory.Packages.props` (central package management).

| Package | Version | Notes |
| --- | --- | --- |
| `Microsoft.Windows.AI.MachineLearning` | 2.4.89 | Ships native ORT 1.27.1 plus DirectML. Pinned directly, because GenAI.WinML's floor is 2.1.1 |
| `Microsoft.ML.OnnxRuntimeGenAI.WinML` | 0.17.1 | Windows-TFM GenAI flavor |
| `Microsoft.ML.OnnxRuntime.Gpu` | `$(OnnxRuntimeVersion)` = 1.30.0 | Windows TFM: `ExcludeAssets="native"`, so only the managed API is used and the native ORT is Windows ML's. Non-Windows TFM: full stock ORT plus `Microsoft.ML.OnnxRuntimeGenAI.Cuda` |

Managed ORT (1.30) is newer than native ORT (1.27.1) on Windows. The binding loads because it requests API v14. Do not call C# APIs that wrap ORT C functions added after 1.27 on the Windows path without a version gate. See the core rules in SKILL.md.

The removed packages `Microsoft.WindowsAppSDK.ML` and `Microsoft.WindowsAppSDK.Runtime` were the old 1.8-era references. Do not re-add them: they would flip deployment mode and the ORT version.

## Where things live

| Concern | File |
| --- | --- |
| Package wiring per TFM, plus the DNNL flavor's `ExcludeAssets="runtime"` updates | `src/Trackdub.Inference.Onnx/Trackdub.Inference.Onnx.csproj` |
| Catalog bootstrap: per-provider `EnsureReadyAsync` + `TryRegister`, with no bulk call on hot paths | `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Which provider uses which registration route (packaged DirectML vs catalog EP), and the rule that session and readiness code must not call bulk `EnsureAndRegisterCertifiedAsync` | `src/Trackdub.Inference.Onnx/WindowsMl/WindowsMlProviderRegistrationPolicy.cs` |
| Single catalog-EP registration | `src/Trackdub.Inference.Onnx/WinMlCatalog/WindowsMlCatalogEpRegistration.cs` |
| MIGraphX via the catalog | `src/Trackdub.Inference.Onnx/Migraphx/WindowsMlMigraphxCatalogService.cs` |
| TRT-RTX bring-your-own EP ABI plugin. The registration name must equal the canonical EP name, or device filtering breaks | `src/Trackdub.Inference.Onnx/TensorRtRtx/TensorRtRtxPluginService.cs` |
| Skipping the GenAI-vs-native ORT version check when the Windows ML runtime is present | `src/Trackdub.Inference.Onnx/Runtime/GenAiNativeCompatibility.cs` (`IsWindowsMlRuntime`) |
| Per-stage provider order; GenAI families exclude the TRT families and DirectML | `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs` (`GenAiProviders`) |
| Smoke tester refuses GenAI on TRT-RTX and DirectML | `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderSmokeTester.cs` |
| VRAM admission factors (measured 1.25x for TRT-RTX and DirectML) | `src/Trackdub.Inference.Onnx/Pool/InferenceSessionPool.cs` |
| GenAI model loading and pooling | `src/Trackdub.Inference.Onnx/Pool/GenAiModelPool.cs` |

Decisions and reference docs:
- ADRs:
  - `docs/decisions/ADR-0001-winui3-windows-ml.md`
  - `docs/decisions/ADR-0002-windows-ml-provider-strategy.md`
  - `docs/decisions/ADR-0017-ort130-inference-worker.md`, amended for Windows ML 2.4 + GenAI.WinML
- Windows ML reference docs:
  - `docs/reference/windows-ml-stage-provider-matrix.md`
  - `docs/reference/windows-ml-phase-3-device-policies.md`
  - `docs/reference/windows-ml-phase-4-closeout.md`
  - `docs/reference/windows-ml-phase-5-catalog-eps.md`
- GPU and TRT-RTX docs:
  - `docs/reference/gpu-execution-providers.md`
  - `docs/reference/tensorrt-rtx-ep-abi-plugin.md`

If connected, the `trackdub-docs-rag` MCP server indexes these docs, along with pinned vendor docs.

## Lessons already paid for

**DirectML vanished because a foreign ORT won.** The desktop app (gated repo, `src/Trackdub.App.Avalonia/Trackdub.App.Avalonia.csproj`) had an `AlignOnnxRuntimeNativeForGenAi` target that copied stock ORT 1.30, which has no DirectML, over Windows ML's `onnxruntime.dll`. The fix moved GenAI to `.WinML` and deleted the target (branch `feat/winml-2.4-desktop`). Any new build step that copies `onnxruntime*.dll` is a regression of this bug.

**GenAI on DirectML is excluded, deliberately.** The bundled GenAI exports are CPU/CUDA exports:
- **Whisper:** terminates the host process inside `Generator.SetInputs` on DirectML.
- **Qwen2.5:** fails its first DirectML kernel with `DmlFusedNode` 0x80070057.
- **Graph capture:** GenAI turns DirectML graph capture on by default. The only working opt-out was `config.SetProviderOption("DML", "enable_graph_capture", "0")`, spelled with an uppercase `DML`. It did not fix the crashes, so the families exclude DirectML instead.

Revisit this only with DirectML-targeted exports.

**GenAI on TRT-RTX is excluded.** It terminates the host with a native stack overflow on the bundled GenAI models.

**The pipeline works on Windows ML 2.4.** Validated end to end with no overrides after PR #418:

| Stage | Provider |
| --- | --- |
| VAD | DirectML |
| Diarization | TRT-RTX |
| ASR (qwen3) | DirectML |
| GenAI | CPU |
| Translation | CPU |
| TTS | CPU |

MADLAD on TRT-RTX (fp16 bucketed) lives on a separate branch.

**Cold start on TRT-RTX is EP-context compile.** Trackdub captures EP context on the first build into a staging folder and publishes it with a stamp listing artifact files, because large engines are external `.engine` sidecars. Treat any cache-key or stamp change as needing the compatibility check described in api-patterns.md.

## Verifying a change here

1. Run `dotnet build` with `-warnaserror`. Run the test projects `Trackdub.Inference.Tests`, `Trackdub.Inference.Onnx.Tests` and `Trackdub.Composition.Tests`.
2. On a real GPU machine, run the app or a probe and check:
   - The loaded ORT version is 1.27.1. If it is not, a foreign `onnxruntime.dll` won.
   - `GetEpDevices()` lists `DmlExecutionProvider`.
   - Catalog EPs show up after registration.
   - One GenAI generation succeeds on CPU.
3. Keep probe executables at short paths (for example `D:\tdhost\...`). Deep scratch paths have broken native DLL loading before.
