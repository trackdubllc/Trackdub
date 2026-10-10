# Windows ML API patterns

C# first, because that is the common case. Sources: Microsoft Learn (`initialize-execution-providers`, `register-execution-providers`, `select-execution-providers`, `model-compilation`, `bring-your-own-eps`, `versioning`), `microsoft/windowsappsdk-samples` `Samples/WindowsML/Shared/cs/ExecutionProviderManager.cs`, and the ORT C# API docs (Context7 `/websites/onnxruntime_ai`). Checked 2026-10-09.

Namespaces: `Microsoft.Windows.AI.MachineLearning` (catalog) and `Microsoft.ML.OnnxRuntime` (everything else).

## Contents

1. Install and register EPs through the catalog
2. See what ORT can use
3. Select devices explicitly
4. Select by policy
5. Compile, cache, and validate EP-context models
6. Bring your own EP
7. Check installed EP versions
8. GenAI on Windows ML
9. Python and C differences

## 1. Install and register EPs through the catalog

**Per provider, with download control.** This is the pattern the official sample uses, and the one to prefer in apps:

```csharp
var catalog = ExecutionProviderCatalog.GetDefault();
foreach (ExecutionProvider provider in catalog.FindAllProviders())
{
    ExecutionProviderReadyState state = provider.ReadyState;

    // NotPresent means EnsureReadyAsync would download through Windows Update.
    if (allowDownload || state != ExecutionProviderReadyState.NotPresent)
    {
        ExecutionProviderReadyResult result = await provider.EnsureReadyAsync();
        if (result.Status != ExecutionProviderReadyResultState.Success)
        {
            // Failure is reported here, not thrown.
            log($"{provider.Name}: 0x{result.ExtendedError?.HResult:X8} {result.DiagnosticText}");
            continue;
        }
        state = provider.ReadyState;
    }

    if (state == ExecutionProviderReadyState.Ready && !provider.TryRegister())
    {
        log($"{provider.Name}: TryRegister returned false");
    }
}
```

| ReadyState | Meaning | Action |
| --- | --- | --- |
| `NotPresent` | Not installed on the device | `EnsureReadyAsync()` downloads, installs, and adds the EP to the app's dependency graph |
| `NotReady` | Installed, but not in this app's dependency graph | `EnsureReadyAsync()`, with no download |
| `Ready` | Usable | `TryRegister()` |

If `EnsureReadyAsync` returns `InProgress`, keep awaiting; it is not a failure. It accepts progress callbacks (`IAsyncOperationWithProgress`), so you can drive a UI progress bar during a first-run download.

**Bulk calls:**

```csharp
await catalog.EnsureAndRegisterCertifiedAsync(); // may download, which can take minutes on first run
await catalog.RegisterCertifiedAsync();          // registers only what is already present, no download
```

Both return the list of providers they handled. Neither installs experimental EPs such as WebGPU; for those, use `EnsureReadyAsync()` + `TryRegister()` explicitly, and remember to unregister before tearing down the environment.

## 2. See what ORT can use

```csharp
foreach (OrtEpDevice d in OrtEnv.Instance().GetEpDevices())
    Console.WriteLine($"{d.EpName} {d.EpVendor} {d.HardwareDevice.Type}");
Console.WriteLine(OrtEnv.Instance().GetVersionString()); // which native ORT actually loaded
```

Before any catalog registration you should see `CPUExecutionProvider (CPU)` and `DmlExecutionProvider (GPU)`. One EP can expose several devices; QNN, for example, shows both an NPU and a GPU entry.

## 3. Select devices explicitly (start here)

```csharp
var devices = env.GetEpDevices()
    .Where(d => string.Equals(d.EpName, "NvTensorRtRtxExecutionProvider", StringComparison.OrdinalIgnoreCase)
             && d.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
    .ToList();
if (devices.Count == 0) { /* fall back: DirectML or CPU */ }

var so = new SessionOptions();
so.AppendExecutionProvider(env, devices, new Dictionary<string, string> { /* EP-specific options */ });
using var session = new InferenceSession(modelPath, so);
```

Rules from the ORT API:
- All devices in one `AppendExecutionProvider` call must belong to the same EP.
- Call it once per EP. Append order is priority order, highest first.
- The `OrtEnv` you pass must be the one that produced the devices.

## 4. Select by policy

```csharp
var so = new SessionOptions();
so.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_EFFICIENCY); // or PREFER_NPU, PREFER_GPU, MAX_PERFORMANCE, ...
```

A policy is convenient, but you cannot see which device was chosen. Use explicit selection until you have measurements; then a policy can be a reasonable default for broad hardware. ETW logs (see execution-providers.md) record the auto-selection decision.

## 5. Compile, cache, and validate EP-context models

Hardware EPs compile the graph to a device binary. That costs seconds to minutes on each session creation unless you cache an EP-context model.

**Compile API** (ORT 1.22+):

```csharp
var compile = new OrtModelCompilationOptions(sessionOptions); // sessionOptions already has the EP appended
compile.SetInputModelPath(modelPath);
compile.SetOutputModelPath(compiledPath);
compile.CompileModel();
```

**Alternative: capture on first session.** Set session config entries:
- `ep.context_enable` = `1`
- `ep.context_file_path` = output path
- `ep.context_embed_mode` = `1` to embed, or `0` for an external binary

Large engines (over about 2 GB) must stay external, so the cache contains sidecar files as well as the `.onnx`.

Some EPs produce no compiled output. If the output file does not exist, fall back to the original model.

**Validate before reuse** (Windows ML 2.3+ / ORT 1.24+):

```csharp
static bool IsCompiledModelOptimal(OrtEnv env, string compiledPath, string epName, IReadOnlyList<OrtEpDevice> devices)
{
    string info = env.GetCompatibilityInfoFromModel(compiledPath, epName);
    return !string.IsNullOrWhiteSpace(info)
        && env.GetModelCompatibilityForEpDevices(devices, info) == OrtCompiledModelCompatibility.EP_SUPPORTED_OPTIMAL;
}
```

| Status | Meaning | Use cached model? |
| --- | --- | --- |
| `EP_SUPPORTED_OPTIMAL` | Supported and optimal | Yes |
| `EP_SUPPORTED_PREFER_RECOMPILATION` | Runs, but the EP recommends a rebuild | Only if you accept suboptimal; otherwise recompile |
| `EP_UNSUPPORTED` | Will not work on these devices | No |
| `EP_NOT_APPLICABLE` | The EP made no determination; this is also the default when the EP does not implement the check | No. This is not proof of compatibility |

Cache policy that survives real fleets:
- Key the cache on the source model hash, the ORT and EP versions, and compile options.
- Store artifacts per device, in local app data.
- Invalidate when any of these change: a new EP is installed, the driver updates, the hardware changes, the source model changes, or the ORT or EP version changes.
- Compile into a staging folder. Validate the new artifact as well, and promote it only when it reports `EP_SUPPORTED_OPTIMAL`.
- If compilation or validation fails, run the original model for that session and never reuse a stale artifact.

To validate before downloading a prebuilt compiled model, publish the compatibility string as metadata beside the artifact and check it first. `GetCompatibilityInfoFromModel` has to parse the whole model.

## 6. Bring your own EP

Use this for offline machines, managed devices, or strict version pinning. Ship the EP DLL with the app and register it into the same process environment:

```csharp
OrtEnv.Instance().RegisterExecutionProviderLibrary("NvTensorRtRtxExecutionProvider", fullPathToEpDll);
```

- Use the canonical EP name as the registration name.
- Use an absolute path.
- Register once per process; guard it with a lock, and remember the registered path.
- The library must be built against an ORT version compatible with the native ORT that Windows ML ships. Windows ML does not guarantee compatibility across its updates, so retest on every Windows ML bump.
- `UnregisterExecutionProviderLibrary` only after every session that uses the EP has been disposed.

Known packages:
- NVIDIA: `NVIDIA/TensorRT-RTX-EP-ABI`
- Intel: `Intel.ML.OnnxRuntime.EP.OpenVINO`
- Qualcomm: `Qualcomm.ML.OnnxRuntime.QNN`

Each adds about 80 MB or more. A hybrid is supported: try the catalog first, and fall back to the bundled EP.

## 7. Check installed EP versions

```csharp
foreach (var p in ExecutionProviderCatalog.GetDefault().FindAllProviders())
{
    var v = p.PackageId?.Version; // null when the EP is not installed
    log(v is null ? $"{p.Name}: not installed" : $"{p.Name}: {v.Value.Major}.{v.Value.Minor}.{v.Value.Build}.{v.Value.Revision}");
}
```

On a dev machine, run `Get-AppxPackage MicrosoftCorporationII.WinML.*` in PowerShell. An app cannot force an EP update; updates arrive through Windows Update.

## 8. GenAI on Windows ML

GenAI uses the process ORT environment. Register EPs first (catalog or BYO), then pick the provider in the GenAI config:

```csharp
using var oga = new OgaHandle();                       // keep alive for the process lifetime
// ... catalog registration as in section 1 ...
using var config = new Config(modelDir);
config.ClearProviders();
config.AppendProvider("NvTensorRtRtx");                 // short name from genai_config.json; "cpu" means append nothing
config.SetProviderOption("NvTensorRtRtx", "key", "value");
using var model = new Model(config);
```

Provider-option keys are case-sensitive about the provider name. Use the same spelling as `AppendProvider`. For DirectML that is `DML`; see trackdub.md for a case where this mattered.

## 9. Python and C differences

**Python.** Do not use the catalog's bulk register APIs: they do not register into the Python ORT environment. Instead:

```python
for p in catalog.find_all_providers():          # catalog from winml / windowsml
    p.ensure_ready()
    if p.library_path:
        ort.register_execution_provider_library(p.name, p.library_path)   # or og.register_execution_provider_library(...)
```

Also for Python:
- Initialize the Windows App SDK first, with the `DynamicDependency.Bootstrap` `initialize(...)` context manager.
- Remove pywinrt's bundled `msvcp140.dll` if it conflicts with other packages.

**C/C++.** The C API has no bulk call. Use:
1. `WinMLEpCatalogCreate` and `WinMLEpCatalogEnumProviders`.
2. `WinMLEpEnsureReady` to prepare the EP.
3. Read the library path, then call `Ort::Env::RegisterExecutionProviderLibrary` and `AppendExecutionProvider_V2`.

CMake consumption: `find_package(microsoft.windows.ai.machinelearning CONFIG REQUIRED)` with `CMAKE_PREFIX_PATH` set to the package's `build/cmake`. The targets are `WindowsML::Api`, `WindowsML::OnnxRuntime` and `WindowsML::DirectML`.
