# ONNX Runtime and GenAI behaviour from source

Tier 2. Every claim below comes from upstream source or tests at the cited commit, not from Microsoft Learn. Each one is true for that version only. When the ORT or GenAI version changes, re-read the cited file before relying on the claim.

Checked 2026-10-09/10 using GitHits against the commits listed.

## Managed ORT binding vs. native ORT version

- **Claim.** The C# binding asks the native library for ORT API version 14 (`const uint ORT_API_VERSION = 14`). It then copies the native `OrtApi` function table into its own struct layout (`api_ = (OrtApi)OrtGetApi(ORT_API_VERSION)`).
- **Source.** `csharp/src/Microsoft.ML.OnnxRuntime/NativeMethods.shared.cs`, `microsoft/onnxruntime@ee5f6e7c` (main, 2026-09-24), lines 538–545.
- **Consequence (inferred from the code, not reproduced).** A managed `Microsoft.ML.OnnxRuntime` newer than the loaded native `onnxruntime.dll` still loads. But function-table slots added after the native version read memory beyond the native table, so calling a C# API that wraps one of those functions is undefined behaviour. Keep the managed and native versions matched, or gate the newer APIs.

## `RegisterExecutionProviderLibrary` registration name

- **Claim.** `registration_name` is the name the library is registered under, and the same name is used to unregister it. ORT passes it to the library's `CreateEpFactories`. The library decides the EP name its devices report: ORT's example plugin comments "Factory could use registration_name or define its own EP name."
- **Sources.**
  - `include/onnxruntime/core/session/onnxruntime_c_api.h` (RegisterExecutionProviderLibrary docs)
  - `include/onnxruntime/core/session/onnxruntime_ep_c_api.h` (`CreateEpFactories`)
  - `onnxruntime/test/autoep/library/example_plugin_ep/example_plugin_ep.cc` line 31

  All at `microsoft/onnxruntime@ee5f6e7c`.
- **Consequence.** Filter devices by the `OrtEpDevice.EpName` the library actually reports. Do not assume it equals the registration name. Learn's C++ sample registering QNN under `"QNN"` is consistent with this (Tier 1).

## GenAI provider names

- **Claim.** `AppendProvider` and `SetProviderOption` normalize the provider name: lower-case, strip a trailing `ExecutionProvider`, then map known aliases.
- **Source.** `NormalizeProviderName` in `src/config.cpp`, `microsoft/onnxruntime-genai@2472a360` (main, 2026-10-09).
- **Not verified for 0.17.1.** That release tag could not be indexed, so whether 0.17.1 normalizes the same way is unknown.

## GenAI and the application's ORT environment

- **Claim.** GenAI's C API exposes `OgaRegisterExecutionProviderLibrary`, which registers into GenAI's own environment (`Generators::GetOrtEnv()`). Python exposes it as `og.register_execution_provider_library`. The C# binding has no wrapper on main. GenAI's C# tests instead register plugin EPs with `OrtEnv.Instance().RegisterExecutionProviderLibrary` before loading models.
- **Sources.** `src/ort_genai_c.cpp` line 2198; `test/csharp/TestOnnxRuntimeGenAIAPI.cs` line 151. Both at `microsoft/onnxruntime-genai@2472a360`.
- **Consequence.** Sharing one environment between the app and GenAI depends on both loading the same `onnxruntime.dll`. Prove it end to end on your build: register an EP, then load a GenAI model on it.

## DirectML graph capture in GenAI

- **Claim.** GenAI enables DirectML graph capture by default and honours an `enable_graph_capture` = `0` provider option.
- **Source.** `src/config.cpp` near line 3077, `microsoft/onnxruntime-genai@2472a360`.

## EP-context capture session keys

- **Claim.** ORT session config entries `ep.context_enable`, `ep.context_file_path` and `ep.context_embed_mode` capture an EP-context model on first session build. This is the alternative to the compile API that Learn documents.
- **Source.** ORT EP-context design documentation and session option key constants (onnxruntime.ai, not Learn). Re-check against the ORT version in use.
