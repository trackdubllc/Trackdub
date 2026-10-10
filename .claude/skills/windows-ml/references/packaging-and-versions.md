# Packaging, deployment, and versions

Checked 2026-10-09 against Microsoft Learn (`distributing-your-app`, `onnx-versions`, `run-genai-onnx-models`, `get-started-models-genai`) and NuGet metadata. Re-check the version table before quoting it.

## Contents

- Packages
- Deployment modes
- Target framework requirements
- ONNX Runtime version shipped by each Windows ML release
- What is in the runtime
- GenAI on Windows ML
- Trimming DirectML
- Checklist before changing a pin

## Packages

| Package | What it is | Min OS (C#/WinRT) |
| --- | --- | --- |
| `Microsoft.Windows.AI.MachineLearning` | Core Windows ML 2.x package: `Microsoft.Windows.AI.MachineLearning.dll`, `onnxruntime.dll`, `DirectML.dll`, winmd, C headers, and CMake config under `build/cmake`. It is self-contained by default. | Windows 10 1903 (18362) |
| `Microsoft.WindowsAppSDK.ML` | Windows App SDK wrapper over the core package. It adds RegFree WinRT for 1809, and it is the package to use for framework-dependent deployment. | Windows 10 1809 (17763) |
| `Microsoft.WindowsAppSDK.Runtime` / `Microsoft.WindowsAppSDK` | Referencing either one switches Windows ML to framework-dependent mode. | — |
| `Microsoft.ML.OnnxRuntimeGenAI.WinML` | ONNX Runtime GenAI built against Windows ML's ORT. It depends on `Microsoft.ML.OnnxRuntimeGenAI.Managed` and `Microsoft.Windows.AI.MachineLearning` (floor only, see below). | Windows-specific TFM required |
| Python: `wasdk-Microsoft.Windows.AI.MachineLearning[all]`, `wasdk-Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap`, `onnxruntime-windowsml` | Framework-dependent only. Needs the matching Windows App SDK Runtime installed, and an unpackaged Python 3.10–3.13 (not the Store build). | — |

Windows App SDK 2.0 (April 2026) split the original ML package: core Windows ML moved into `Microsoft.Windows.AI.MachineLearning`, and `Microsoft.WindowsAppSDK.ML` now depends on it. To learn which ORT you actually get through `Microsoft.WindowsAppSDK.ML`, look at its dependency on the core package.

## Deployment modes

| | Self-contained | Framework-dependent |
| --- | --- | --- |
| Languages | C#, C++/WinRT, C/C++ | C#, C++/WinRT, Python (C/C++ is not supported) |
| C# packages | `Microsoft.Windows.AI.MachineLearning` (or `Microsoft.WindowsAppSDK.ML`). Do **not** add `.Runtime` or the main `Microsoft.WindowsAppSDK` package. | `Microsoft.WindowsAppSDK.ML` + `Microsoft.WindowsAppSDK.Runtime`, or the main `Microsoft.WindowsAppSDK` package (≥ 1.8.1) with `WindowsAppSDKSelfContained` unset or `false` |
| Size | About 41 MB larger | Shared system-wide |
| Updates | Only when you ship a new version | Through Windows App SDK servicing |
| Best for | Strict version control | Smallest install size, Store apps |

If you must reference the main `Microsoft.WindowsAppSDK` package but want self-contained deployment, set `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`.

The vendor EPs (QNN, VitisAI, OpenVINO, NvTensorRtRtx, MIGraphX) are never part of the runtime in either mode. They come from the catalog, or you bundle them yourself.

## Target framework requirements (C#)

- .NET 8 or later to get the `Microsoft.ML.OnnxRuntime` APIs. .NET 6 can call the catalog, but cannot use ORT.
- The TFM must be at least `net8.0-windows10.0.18362.0` for `Microsoft.Windows.AI.MachineLearning`, or at least `net8.0-windows10.0.17763.0` for `Microsoft.WindowsAppSDK.ML`.
- A multi-targeted library that also builds for `net8.0`/`net10.0` (Linux and macOS) needs conditional `PackageReference`s, plus `Compile Remove` for files that use `Microsoft.Windows.AI.MachineLearning` types.

## ONNX Runtime version shipped by each Windows ML release

From Learn's `onnx-versions` page, retrieved 2026-10-09. **Only the current release is officially supported.**

| Microsoft.Windows.AI.MachineLearning | Released | ORT |
| --- | --- | --- |
| **2.4.89 (current)** | 2026-09-22 | 1.27.1 |
| 2.7.2021-experimental | 2026-10-07 | 1.30.0 |
| 2.6.74-rc | 2026-09-17 | 1.30.0 |
| 2.5.83-rc | 2026-09-28 | 1.28.2 |
| 2.4.66-preview | 2026-07-15 | 1.27.1 |
| 2.3.42 | 2026-08-28 | 1.27.1 |
| 2.2.12 | 2026-07-28 | 1.25.2 (commit 94bc0cd) |
| 2.1.74 / 2.1.71 / 2.1.70 / 2.1.6 / 2.1.1 | 2026-05 to 2026-07 | 1.24.6 (commit 800ac32) |
| 2.0.300 | 2026-04-28 | 1.24.5 (commit ef605cd) |

Release dates do not follow version order (2.3.42 shipped after 2.4.66-preview). Compare versions, not dates.

Compiled-model compatibility APIs (`GetModelCompatibilityForEpDevices`, `GetCompatibilityInfoFromModel`) require Windows ML 2.3 or later.

## What is in the runtime

| Binary | Purpose | Size |
| --- | --- | --- |
| `Microsoft.Windows.AI.MachineLearning.dll` | Catalog and other Windows ML WinRT APIs | ~1 MB |
| `onnxruntime.dll` | ORT engine | ~20 MB |
| `DirectML.dll` | Included GPU EP | ~20 MB |

## GenAI on Windows ML

- `Microsoft.ML.OnnxRuntimeGenAI.WinML` runs GenAI on Windows ML's ORT. GenAI libraries are still 0.x previews, so expect API churn.
- **Do not reference more than one GenAI flavor.** `.WinML`, `.DirectML`, `.QNN`, `.Cuda` and CPU `Microsoft.ML.OnnxRuntimeGenAI` each ship conflicting `onnxruntime.dll` files. The GenAI API (`Model`, `Config`, `Tokenizer`, `Generator`) is identical across flavors.
- **The Windows ML dependency is a floor, not a pairing.** GenAI.WinML 0.17.1 declares `Microsoft.Windows.AI.MachineLearning >= 2.1.1`. NuGet picks the lowest matching version (2.1.1, which ships ORT 1.24.6) unless the project references Windows ML directly. Pin `Microsoft.Windows.AI.MachineLearning` explicitly. Read `requested`/`dependencies` in `packages.lock.json`, not a registry tool's "resolved latest".
- GenAI and the app share one process ORT environment. An EP registered through the catalog, or with `OrtEnv.Instance().RegisterExecutionProviderLibrary`, is visible to GenAI. Select it in GenAI with `config.ClearProviders(); config.AppendProvider("<short name>")`, then `config.SetProviderOption(...)`. Provider short names follow `genai_config.json`, for example `dml`, `cuda` and `NvTensorRtRtx`. The full ORT names are also accepted.
- MIGraphX is documented as not supported for GenAI.

## Trimming DirectML (self-contained, unsupported but documented)

If the app never appends the DirectML EP, a post-build `Delete` of `$(OutDir)DirectML.dll` and `$(OutDir)runtimes\win-*\native\DirectML.dll` (and the same under `$(PublishDir)`) halves the footprint. This relies on the package's internal layout, so retest after every package update. With CMake consumption, `DirectML.dll` is excluded by default.

## Checklist before changing a pin

1. Look up the new release's ORT version in the table above, or better, fetch the live `onnx-versions` page.
2. Compare that against every managed ORT package version in the solution (see the core rules on managed vs native ORT).
3. Grep build targets and scripts for anything that copies `onnxruntime*.dll` into output folders.
4. Run a locked restore and update the lock files.
5. On a real device: print the loaded ORT version, list EP devices, and run one session per shipped EP plus one GenAI generation.
