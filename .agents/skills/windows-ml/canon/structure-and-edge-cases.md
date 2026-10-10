# Structural details and edge cases

Tier 1: Microsoft Learn only. Checked 2026-10-10.

## Runtime deployment and EP sourcing are independent

Self-contained/framework-dependent describes the runtime; catalog/bundled describes EP acquisition. Bundling the runtime does not bundle vendor EPs automatically. [Deployment](https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app), [EP sourcing](https://learn.microsoft.com/windows/ai/new-windows-ml/windows-ml-eps-vs-bring-your-own).

## Minimum Windows target differs from catalog availability

A compiling Windows TFM does not prove catalog acceleration is available: catalog-acquired hardware EPs need Windows 11 24H2+, while CPU/included DirectML have broader coverage. [Getting started](https://learn.microsoft.com/windows/ai/new-windows-ml/get-started), [providers](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers).

## Installed, prepared and registered are separate

`NotPresent` means absent. `NotReady` means installed but absent from the app's runtime dependency graph. `Ready` still needs ORT registration. `EnsureReadyAsync()` can download/install and change the dependency graph; it is not merely a read-only status check. [Installation states](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers).

## Device lists change

EP/driver updates can add/remove EP devices. Enumerate current devices and apply the same group when configuring sessions and validating compiled artifacts. [Selection](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers), [compilation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

## Runtime and EP updates differ

Framework-dependent runtime servicing and catalog EP updates can change behavior without an app release. EP updates arrive via Windows Update; applications currently cannot install an EP update programmatically. [Runtime versions](https://learn.microsoft.com/windows/ai/new-windows-ml/onnx-versions), [EP updates](https://learn.microsoft.com/windows/ai/new-windows-ml/update-execution-providers).

## Larger version numbers do not establish production support

Microsoft separates the current supported release from historical, preview and experimental releases. Read the Windows ML/ORT mapping instead of inferring native ORT from package numbering. WebGPU and Runtime API experimental requirements are distinct. [Versions](https://learn.microsoft.com/windows/ai/new-windows-ml/onnx-versions), [WebGPU](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep), [Runtime API](https://learn.microsoft.com/windows/ai/new-windows-ml/runtime/overview).

## Offline and managed environments

Pending reboots, paused Windows Update and enterprise policy can block EP downloads. Microsoft describes bundled/hybrid sourcing for controlled environments; the app owns bundled compatibility validation. Do not silently change update policy while diagnosing. [Download failures](https://learn.microsoft.com/windows/ai/new-windows-ml/execution-provider-errors), [sourcing](https://learn.microsoft.com/windows/ai/new-windows-ml/windows-ml-eps-vs-bring-your-own).

## Cache validity has two dimensions

Validate EP/device compatibility and source-model identity. Device, driver, runtime, EP, compile settings or source changes can invalidate artifacts. Remote metadata must match the exact compiled artifact. File existence and `EP_NOT_APPLICABLE` are not validation. [Compilation](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

## Compatibility APIs are version-dependent

Microsoft specifies Windows ML 2.3+ for compiled-model compatibility APIs. Re-fetch prerequisites before introducing API calls. [Prerequisites](https://learn.microsoft.com/windows/ai/new-windows-ml/model-compilation).

## Language examples are not interchangeable

Python's ORT environment needs individual EP registration; C/C++ lacks C#'s single-call bulk APIs. Use the correct language tab. [Install](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers), [register](https://learn.microsoft.com/windows/ai/new-windows-ml/register-execution-providers).

## Available EP does not mean full graph acceleration

Microsoft's WebGPU page explicitly describes unsupported operators partitioning onto CPU and performance implications. Use logs to understand actual selection; do not generalize identical behavior to every EP. [WebGPU](https://learn.microsoft.com/windows/ai/new-windows-ml/webgpu-ep), [logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).

## Diagnostics have separate privilege requirements

ETW capture with WPR requires administrator privileges and Windows Performance Toolkit. Keep traces short; do not treat capture privileges as ordinary inference requirements. [Logs](https://learn.microsoft.com/windows/ai/new-windows-ml/logs).

## Licenses are component-specific

Windows ML licensing does not replace vendor EP terms. Microsoft's notices do not establish an individual model's rights. [Third-party notices](https://learn.microsoft.com/windows/ai/new-windows-ml/third-party-notices).

## Unspecified application details

Process/worker architecture, pool keys, retry/cancellation policy, stage exclusions and measured readiness are application decisions. Do not import them into this skill. If Microsoft Learn is silent, say the detail is unspecified rather than inventing a platform contract.
