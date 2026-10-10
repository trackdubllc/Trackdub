---
name: windows-ml
description: Windows ML (WinML, Microsoft.Windows.AI.MachineLearning) engineering guide. Covers choosing NuGet packages and deployment mode, matching the bundled ONNX Runtime version, installing and registering execution providers through ExecutionProviderCatalog, selecting OrtEpDevices, compiling and caching EP-context models, running ONNX Runtime GenAI on Windows ML, and diagnosing DirectML, NvTensorRtRtx, QNN, OpenVINO, VitisAI, MIGraphX and WebGPU registration failures. Use this skill whenever work touches Windows ML or WinML, ExecutionProviderCatalog, EnsureReadyAsync/TryRegister, OrtEpDevice or GetEpDevices on Windows, onnxruntime.dll version conflicts, Microsoft.ML.OnnxRuntimeGenAI.WinML, DirectML being unavailable, or upgrading the Windows ML / ONNX Runtime pin — even when the user only says something like "DirectML is broken", "which ORT do we ship on Windows" or "why doesn't the NPU show up".
---

# Windows ML

Windows ML is the Windows-maintained build of ONNX Runtime (ORT) plus an execution-provider (EP) catalog. Your inference code is ordinary ORT code (`Microsoft.ML.OnnxRuntime` in C#, the ORT C/C++ API, or `onnxruntime` in Python). Windows ML adds two things on top:

1. **A shared or self-contained `onnxruntime.dll`** that Microsoft builds and services, with `DirectML.dll` beside it.
2. **`ExecutionProviderCatalog`**, a WinRT API that downloads vendor EPs through Windows Update, adds them to the app's package graph, and registers their DLLs into the process ORT environment.

Once an EP is registered, nothing is Windows ML-specific any more: you enumerate `OrtEpDevice`s, append the ones you want to `SessionOptions` and create sessions as usual. Most bugs come from the seams: the wrong `onnxruntime.dll` loading, an EP that was never registered, or a cached compiled model reused on the wrong driver.

Facts below were checked on 2026-10-09. Windows ML ships often (2.x versions arrive roughly monthly), so re-verify version numbers before you state them. See "Verify before asserting" below.

## Working in the Trackdub repo?

Read [references/trackdub.md](references/trackdub.md) first. It records the pinned versions, which source files own EP registration, the ADRs, and DirectML/GenAI failures already measured on this codebase. Re-deriving those costs hours.

## Pick the reference you need

| Task | Read |
| --- | --- |
| Adding or upgrading packages, choosing self-contained vs framework-dependent, finding which ORT version a Windows ML release ships, pairing GenAI with Windows ML | [references/packaging-and-versions.md](references/packaging-and-versions.md) |
| Writing or reviewing code: catalog init, per-EP install and registration, device selection, policies, compile and cache validation, bring-your-own EPs, checking EP versions | [references/api-patterns.md](references/api-patterns.md) |
| EP names, hardware and driver requirements, ReadyState meanings, update behaviour, download failures, ETW logs | [references/execution-providers.md](references/execution-providers.md) |

## Core rules (the ones that bite)

**One ORT per process, and it must be Windows ML's.** Windows ML's EPs and DirectML are built against the `onnxruntime.dll` in the Windows ML package. If another package drops a different `onnxruntime.dll` into the output folder, or a build step copies one over it, that one loads instead. DirectML or catalog EPs then disappear, or the process crashes. Concretely:
- If you need a stock ORT package for its managed API, reference it with `ExcludeAssets="native"`.
- Reference only one GenAI flavor. `.WinML`, `.DirectML`, `.Cuda`, `.QNN` and plain `Microsoft.ML.OnnxRuntimeGenAI` each ship a conflicting `onnxruntime.dll`.
- After a build, check which `onnxruntime.dll` sits next to `onnxruntime-genai.dll` and next to the app executable.

**Managed ORT newer than native ORT is a loaded gun.** The C# binding asks native ORT for API version 14, so a newer `Microsoft.ML.OnnxRuntime` managed assembly still loads against Windows ML's older native ORT. But any C# method that wraps a C API added after the native version has no valid function-pointer slot, and calling it is undefined (usually a crash). Either keep managed and native on the same minor version, or know exactly which APIs you call and gate the newer ones.

**Registration is per process and does not throw on failure.**
- `EnsureReadyAsync()` reports failure through `ExecutionProviderReadyResult.Status`, `ExtendedError` (an HRESULT) and `DiagnosticText`. Always check the status.
- `TryRegister()` only succeeds when `ReadyState == Ready`, and it returns `false` rather than throwing.
- An EP that was never registered is simply absent from `GetEpDevices()`. Callers therefore see "provider not available" with no root cause. Log the ready result.

**Bulk catalog calls are slow and download.** `EnsureAndRegisterCertifiedAsync()` can take seconds to minutes on first run, needs Windows Update, and skips experimental EPs such as WebGPU. Keep it off session-creation and readiness hot paths. Use `RegisterCertifiedAsync()` (no download) or per-provider `EnsureReadyAsync` + `TryRegister` where latency matters.

**Device lists change under you.** Windows Update services EPs through the optional "D week" releases, and drivers update too. Re-enumerate `GetEpDevices()` rather than caching `OrtEpDevice` objects across app lifetimes. Expect devices to appear and vanish.

**Match EP names case-insensitively and register under the canonical name.**
- The catalog's `ExecutionProvider.Name` is the canonical EpName, for example `NvTensorRtRtxExecutionProvider`.
- Some upstream samples spell the same name with different casing, for example `NvTensorRTRTXExecutionProvider` in the GenAI C# examples.
- Compare with `OrdinalIgnoreCase`.
- When you call `RegisterExecutionProviderLibrary` yourself, use the canonical EP name as the registration name, so device filtering by name keeps working.

**DirectML is "included" but legacy.** CPU and DirectML need no catalog call and work on every supported OS. The catalog EPs need Windows 11 24H2 (build 26100) or later, plus a supported device and driver. Treat DirectML as the universal GPU fallback, not the fast path.

**Compiled EP-context models are device- and driver-specific.**
- Before reusing a cached `*_ctx.onnx`, validate it with `GetCompatibilityInfoFromModel` + `GetModelCompatibilityForEpDevices`.
- Accept only `EP_SUPPORTED_OPTIMAL`. `EP_NOT_APPLICABLE` is *not* evidence of compatibility.
- Key the cache on the source model hash too: the compatibility API does not know whether the source model changed.

**Python is different.** The catalog's bulk register APIs do not register into the Python ORT environment. Register each EP with `ort.register_execution_provider_library(provider.name, provider.library_path)` after `ensure_ready`, or `og.register_execution_provider_library(...)` for GenAI.

## Workflows

### Upgrading Windows ML

1. Look up the target `Microsoft.Windows.AI.MachineLearning` version and its ORT version on the Learn page "ONNX Runtime versions shipped in Windows ML". Only the current release is officially supported; `-preview`, `-rc` and `-experimental` builds are history or preview only.
2. Check every package that carries native ORT, GenAI or EP DLLs for conflicts (see the core rules). Read the dependency *ranges* in the lock file. Registry tools that show a "resolved" latest version can be misleading: for example, GenAI.WinML 0.17.1 only requires Windows ML ≥ 2.1.1, and NuGet will pick 2.1.1 (ORT 1.24.6) unless you reference Windows ML directly.
3. Restore with locked mode and regenerate lock files deliberately.
4. Build, then run the following on a real Windows device, not just unit tests:
   - Print `OrtEnv.GetVersionString()`, so you can see which native ORT actually loaded.
   - Enumerate `GetEpDevices()` after registration.
   - Run one session per EP you ship.

### Adding hardware acceleration to an app

1. Start on CPU with plain ORT code. Confirm the model runs.
2. Call the catalog (per-provider or bulk), check ready results, and register.
3. Choose devices explicitly at first (`GetEpDevices` → filter by `EpName` and `HardwareDevice.Type` → `AppendExecutionProvider`). Move to `SetEpSelectionPolicy` (`PREFER_NPU`, `MAX_PERFORMANCE`, `MAX_EFFICIENCY`, …) only after explicit selection works, because a policy hides which device you got.
4. Pre-compile or capture EP context on first run, cache it per device, and validate it before reuse.
5. Offer CPU fallback when session creation or the first `Run` fails on an accelerator.

### Diagnosing "EP missing" or "DirectML broken"

Work from the bottom up. Each step rules out a layer:

1. **Which native ORT loaded?** Use `OrtEnv.Instance().GetVersionString()`, or the loaded-module list. A version different from the Windows ML table means a foreign `onnxruntime.dll` won.
2. **What is registered?** Dump `GetEpDevices()`: EpName, EpVendor, HardwareDevice.Type. Without catalog registration you should see `CPUExecutionProvider` and `DmlExecutionProvider`. No `DmlExecutionProvider` points to the wrong ORT or a missing `DirectML.dll`.
3. **What does the catalog think?** Use `FindAllProviders()` → `Name`, `ReadyState`, and `PackageId?.Version`, plus `EnsureReadyAsync` status, HRESULT and DiagnosticText. On a dev box, `Get-AppxPackage MicrosoftCorporationII.WinML.*` shows installed EP packages.
4. **Download failures:** look for a pending reboot, paused Windows Update, or enterprise policy. If the download still fails, capture ETW with `WinML.wprp` and generate a rundown using `Get-WinMLRundown.ps1` from WindowsAppSDK-Samples. File through Feedback Hub (Developer Platform → Windows Machine Learning).
5. **Only then** suspect the model, the EP options or the driver.

## Verify before asserting

Versions, EP lists and driver minimums change. Before quoting them, or when this skill is silent on something, check a primary source:

- **Microsoft Learn MCP** (`microsoft_docs_search`, then `microsoft_docs_fetch`). Key pages under `https://learn.microsoft.com/windows/ai/new-windows-ml/`:
  - `onnx-versions`: Windows ML to ORT version table.
  - `supported-execution-providers`
  - `distributing-your-app`
  - `initialize-execution-providers`
  - `register-execution-providers`
  - `select-execution-providers`
  - `model-compilation`
  - `bring-your-own-eps`
  - `run-genai-onnx-models`
  - `execution-provider-errors`
  - `logs`
  - `versioning`
- **Context7.** It has no Windows ML library. Use it for ORT and GenAI API details:
  - `/websites/onnxruntime_ai`: C# `OrtEnv`, `SessionOptions`, plugin EP docs.
  - `/microsoft/onnxruntime-genai`: GenAI `Config`, `AppendProvider`, `SetProviderOption`, EP registration.
- **GitHits** (call `quick_start` once per session unless the `githits-mcp` skill is loaded):
  - `pkg_info` / `pkg_deps` on `nuget:Microsoft.Windows.AI.MachineLearning` and `nuget:Microsoft.ML.OnnxRuntimeGenAI.WinML`. Remember the resolved-version caveat above.
  - Canonical sample code in `github:microsoft/windowsappsdk-samples` under `Samples/WindowsML/` (see `Shared/cs/ExecutionProviderManager.cs`).
  - GenAI's Windows ML usage in `github:microsoft/onnxruntime-genai`, in `examples/python/winml.py` and `examples/csharp/Common/Common.cs`.
- **EP release history:** the Windows ML Execution Provider Releases wiki at `github.com/microsoft/WindowsML/wiki`.

When sources disagree, prefer the newest Learn page for Windows ML behaviour, ORT source for ORT behaviour, and measurements on the target machine over both.
