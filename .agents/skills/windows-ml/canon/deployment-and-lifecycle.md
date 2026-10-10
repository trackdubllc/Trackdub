# Deployment and the install → register → select lifecycle

Tier 1: Microsoft Learn only. Checked 2026-10-10. Contains no Trackdub decisions.

## Contents

- Two independent deployment choices
- Packages and target frameworks
- ONNX Runtime version in each Windows ML release
- Lifecycle at a glance
- C# recipes
- Python and C differences

## Two independent deployment choices

Runtime deployment and EP sourcing are separate decisions.

| Runtime mode | Microsoft-documented trade-off |
| --- | --- |
| Self-contained | Runtime binaries (about 41 MB) ship with the app; no separately installed runtime; updates need an app release |
| Framework-dependent | Smaller app; the shared Windows App SDK runtime must be installed; servicing updates the runtime automatically |

The Windows ML runtime is `Microsoft.Windows.AI.MachineLearning.dll`, `onnxruntime.dll` and `DirectML.dll`. Vendor EPs are never part of it. ([Install and deploy](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app))

| EP sourcing | Microsoft-documented trade-off |
| --- | --- |
| Windows ML catalog | Windows-certified combinations, smaller app, automatic EP updates. Needs Windows 11 24H2 or later, plus Windows Update, a network connection and a permitting policy |
| Bring your own | Ship EP binaries; manual updates; the application owns runtime/EP compatibility validation. Suits offline use or strict version pinning |
| Hybrid | Try the catalog, then fall back to a bundled EP for the same hardware |

([Catalog vs. bring-your-own](https://learn.microsoft.com/windows/ai/new-windows-ml/windows-ml-eps-vs-bring-your-own), [bring your own EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/bring-your-own-eps))

## Packages and target frameworks

| Package | Role (per Learn) |
| --- | --- |
| `Microsoft.Windows.AI.MachineLearning` | Core Windows ML package. Self-contained by default. Minimum Windows 10 build 18362 |
| `Microsoft.WindowsAppSDK.ML` | Windows App SDK wrapper. Self-contained on its own; adds RegFree WinRT support for build 17763 |
| `Microsoft.WindowsAppSDK.Runtime` or the main `Microsoft.WindowsAppSDK` | Referencing either switches to framework-dependent deployment. The main package can be forced self-contained with `WindowsAppSDKSelfContained=true` |

C# needs .NET 8 or later for the full API surface; .NET 6 can use the catalog but not the ONNX Runtime APIs. The target framework must be at least `net8.0-windows10.0.18362.0` for the core package, or `net8.0-windows10.0.17763.0` for `Microsoft.WindowsAppSDK.ML`. x64 and ARM64 are supported. ([Install and deploy](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app), [get started](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started))

For GenAI, Learn documents `Microsoft.ML.OnnxRuntimeGenAI.WinML`. It warns not to reference more than one GenAI package flavor (`.DirectML`, `.QNN` and the CPU package), because they ship conflicting `onnxruntime.dll` files. ([GenAI with Windows ML](https://learn.microsoft.com/windows/ai/new-windows-ml/run-genai-onnx-models), [GenAI packages](https://learn.microsoft.com/windows/ai/models/get-started-models-genai))

Learn documents one optional, explicitly unsupported trim for self-contained apps that never use DirectML: delete `DirectML.dll` after build and publish. It depends on the package's internal layout, so retest after every package update. ([Install and deploy](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app))

## ONNX Runtime version in each Windows ML release

Read the live [ONNX Runtime versions](https://learn.microsoft.com/windows/ai/new-windows-ml/onnx-versions) page; do not infer it from package numbering. Only the current release is officially supported; `-preview`, `-rc` and `-experimental` builds are history only. On 2026-10-10 the current release was `2.4.89`, shipping ORT `1.27.1`.

## Lifecycle at a glance

| Step | API | Meaning |
| --- | --- | --- |
| Discover | `ExecutionProviderCatalog.GetDefault().FindAllProviders()` | Compatible catalog EPs, including ones not installed |
| Prepare or install | `provider.EnsureReadyAsync()` | Downloads if needed and adds the EP to the app's runtime dependency graph |
| Register | `provider.TryRegister()` | Registers a `Ready` EP with ONNX Runtime |
| Register installed | `catalog.RegisterCertifiedAsync()` | Registers already-installed certified EPs, with no download |
| Install and register | `catalog.EnsureAndRegisterCertifiedAsync()` | Downloads compatible certified EPs if needed (seconds to minutes), then registers them. Does not install WebGPU |
| Enumerate devices | `OrtEnv.Instance().GetEpDevices()` | EP/device pairs available for selection |
| Select | `SessionOptions.AppendExecutionProvider(env, devices, options)` | Bind explicit devices and options |
| Create session | `new InferenceSession(modelPath, sessionOptions)` | Create the model session |

| `ReadyState` | Meaning | Next step |
| --- | --- | --- |
| `NotPresent` | Not installed on the device | `EnsureReadyAsync()` downloads and installs it |
| `NotReady` | Installed but not in the app's dependency graph | `EnsureReadyAsync()` |
| `Ready` | Usable | `TryRegister()` |

Check `ExecutionProviderReadyResult.Status` before registering:
- `Success`: register the EP.
- `Failure`: inspect `ExtendedError` (an HRESULT) and `DiagnosticText`.
- `InProgress`: the operation hasn't completed. Await it; it is not a failure.

Sources: [install](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [register](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers), [select](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers), [run models](https://learn.microsoft.com/windows/ai/new-windows-ml/run-onnx-models).

## C# recipes

Adapted from the Learn pages above. Namespaces: `Microsoft.Windows.AI.MachineLearning` (catalog) and `Microsoft.ML.OnnxRuntime`.

**Install and register one provider, with download control.**

```csharp
var catalog = ExecutionProviderCatalog.GetDefault();
foreach (ExecutionProvider provider in catalog.FindAllProviders())
{
    if (!allowDownload && provider.ReadyState == ExecutionProviderReadyState.NotPresent)
    {
        continue; // EnsureReadyAsync would download through Windows Update.
    }

    ExecutionProviderReadyResult result = await provider.EnsureReadyAsync();
    switch (result.Status)
    {
        case ExecutionProviderReadyResultState.Success:
            if (!provider.TryRegister())
            {
                log($"{provider.Name}: registration returned false");
            }
            break;
        case ExecutionProviderReadyResultState.InProgress:
            // Not a failure: the install has not finished. Retry later instead of registering now.
            log($"{provider.Name}: install still in progress");
            break;
        default:
            log($"{provider.Name}: 0x{result.ExtendedError?.HResult:X8} {result.DiagnosticText}");
            break;
    }
}
```

`EnsureReadyAsync` also reports progress, which can drive a download progress bar.

**See what ONNX Runtime can use.** Before any catalog registration, Learn's example output lists only `CPUExecutionProvider` and `DmlExecutionProvider`.

```csharp
foreach (OrtEpDevice d in OrtEnv.Instance().GetEpDevices())
    Console.WriteLine($"{d.EpName} ({d.HardwareDevice.Type})");
```

**Select devices explicitly first.** Filter by the EP name and hardware type you want, append them, then create the session. The device list can change after EP or driver updates, so enumerate it at the point of use.

```csharp
var devices = env.GetEpDevices()
    .Where(d => d.EpName == epName && d.HardwareDevice.Type == OrtHardwareDeviceType.NPU)
    .ToList();
if (devices.Count == 0) { /* fall back */ }
var so = new SessionOptions();
so.AppendExecutionProvider(env, devices, epOptions);
```

**Select by policy once explicit selection works.**

```csharp
so.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_EFFICIENCY); // or PREFER_NPU, MAX_PERFORMANCE, ...
```

**Bring your own EP.** Register the shipped library into the same environment:

```csharp
OrtEnv.Instance().RegisterExecutionProviderLibrary("XYZExecutionProvider", "XYZ.EP.dll");
```

Learn's C++ installation example registers QNN under `"QNN"`, so the first argument is a registration name of your choosing. How it relates to `OrtEpDevice.EpName` is not documented on Learn; see [upstream/ort-and-genai-source.md](../upstream/ort-and-genai-source.md). Known bundled packages are NVIDIA `TensorRT-RTX-EP-ABI`, `Intel.ML.OnnxRuntime.EP.OpenVINO` and `Qualcomm.ML.OnnxRuntime.QNN`. You must validate each Windows ML, ORT and EP combination yourself. ([Bring your own EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/bring-your-own-eps))

**Check installed EP versions.** `ExecutionProvider.PackageId` is null when the EP is not installed. On a developer machine, `Get-AppxPackage MicrosoftCorporationII.WinML.*` lists installed EP packages. ([Versioning](https://learn.microsoft.com/windows/ai/new-windows-ml/versioning))

## Python and C differences

- **Python.** The catalog's bulk registration does not reach Python's ORT environment. For each provider, call `result = provider.ensure_ready_async().get()`. If `result.status == winml.ExecutionProviderReadyResultState.SUCCESS`, call `ort.register_execution_provider_library(provider.name, provider.library_path)`. Initialize the Windows App SDK first, and remove pywinrt's bundled `msvcp140.dll` if it conflicts. ([Register](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers), [API notes](https://learn.microsoft.com/windows/ai/new-windows-ml/api-reference))
- **C/C++.** There is no single bulk call. Enumerate with `WinMLEpCatalogCreate` and `WinMLEpCatalogEnumProviders`, prepare with `WinMLEpEnsureReady`, then register with `Ort::Env::RegisterExecutionProviderLibrary`. Consume through CMake via `find_package(microsoft.windows.ai.machinelearning CONFIG REQUIRED)`, with targets `WindowsML::Api`, `WindowsML::OnnxRuntime` and `WindowsML::DirectML`. Framework-dependent deployment is not available for C/C++. ([Install](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [deployment](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app))
