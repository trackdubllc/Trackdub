# Execution providers, requirements, and diagnostics

Sources: Learn `supported-execution-providers`, `update-execution-providers`, `versioning`, `execution-provider-errors`, `logs`, and `webgpu-ep`. Checked 2026-10-09. Driver minimums change, so fetch the live page before quoting one.

## Included (no catalog call, every supported OS)

| EP | EpName | Notes |
| --- | --- | --- |
| CPU | `CPUExecutionProvider` | Always present |
| DirectML | `DmlExecutionProvider` | Docs label it "legacy". Any DirectX 12 GPU. `DirectML.dll` ships beside Windows ML's `onnxruntime.dll`, and it disappears if a foreign ORT loads |

## Catalog EPs (Windows ML 2.x; Windows 11 24H2 / build 26100 or later)

| EP | EpName | Vendor | Requirements (as documented) |
| --- | --- | --- | --- |
| MIGraphX | `MIGraphXExecutionProvider` | AMD | RDNA 3 or later GPU, driver 25.10.13.09 or later. **Not supported for GenAI.** |
| NvTensorRtRtx | `NvTensorRtRtxExecutionProvider` | NVIDIA | GeForce RTX 30xx or later; recommended driver 32.0.15.5585 or later plus CUDA 12.5 |
| OpenVINO | `OpenVINOExecutionProvider` | Intel | See Intel's "Windows ML support" guide |
| QNN | `QNNExecutionProvider` | Qualcomm | Snapdragon X Elite / X Plus Hexagon NPU, driver 30.0.140.0 or later |
| VitisAI | `VitisAIExecutionProvider` | AMD | Adrenalin 25.6.3 with NPU driver 32.00.0203.280 (min), up to Adrenalin 25.9.1 with NPU 32.00.0203.297 (max documented) |
| WebGPU (experimental) | `WebGpuExecutionProvider` | Microsoft | Needs Windows ML ≥ 2.4.66-preview. Not installed by `EnsureAndRegisterCertifiedAsync`. Package family `Microsoft.WinML.ONNX.WebGPU.EP.2`. FP32/FP16 plus int4/int8 for LLMs only |

Windows ML 1.8.x had the same list minus WebGPU. Per-EP version history: the Windows ML Execution Provider Releases wiki (`github.com/microsoft/WindowsML/wiki`).

"Available" means available *subject to device and driver compatibility*. `FindAllProviders()` lists only EPs compatible with this device, so a missing entry often means unsupported hardware or driver, not a bug.

## How EPs update

- Installed catalog EPs update through Windows Update's optional non-security preview ("D week") releases.
- Apps cannot trigger an EP update programmatically.
- Consequences:
  - `OrtEpDevice` lists and EP versions change between runs.
  - Compiled-model caches go stale.
  - Bugs can come and go without any app change.
- Log `ExecutionProvider.PackageId.Version` with every bug report.

## Choosing catalog EPs vs bring-your-own

| | Catalog (recommended) | Bring your own |
| --- | --- | --- |
| Setup | One API call | One NuGet or binary per EP |
| Certification and compatibility | Microsoft validates Windows ML + ORT + EP | You validate every combination |
| App size | Small | About 80–120 MB per EP |
| Updates | Automatic | Manual |
| OS | Windows 11 24H2 or later | Depends on the EP |
| Network on first run | Yes, if not yet installed | No |
| Managed devices | Needs Windows Update permitted by policy | Works |

A hybrid is supported: try the catalog, and fall back to a bundled EP for the same hardware.

## Download and registration failures

In this order:

1. **Pending reboot.** An upstream Windows Update may need a restart before EP download resumes.
2. **Windows Update paused.** Settings → Windows Update → Resume updates.
3. **Managed device.** Enterprise policy may block component downloads. This is an IT question, not a code bug.
4. Read `ExecutionProviderReadyResult.ExtendedError` (HRESULT) and `DiagnosticText` from `EnsureReadyAsync`.
5. Still stuck: report through Feedback Hub → Developer Platform → Windows Machine Learning. It attaches the logs the team needs.

## ETW logs

Get these three files from `microsoft/WindowsAppSDK-Samples` → `Samples/WindowsML/capture-logs/`:
- `Get-WinMLRundown.ps1`
- `WinML.wprp`
- `WindowsMLProfile.wpaProfile`

Capturing needs:
- Admin rights.
- Windows Performance Toolkit, for `wpr`/`wpaexporter`.
- An unrestricted execution policy for the script session.

Keep traces short.

The rundown log (`WinmlRundown.log`), grouped by process, shows:
- the ORT version and whether the redistributable is in use
- the Windows ML version
- GPU and NPU driver info
- session creation (EP ID, FP16, weight type, graph and weight hashes)
- EP auto-selection (policy, requested and available EPs)
- registered EP packages
- error events with HRESULT, file, function and line

Use it to answer "which EP did the policy actually pick?" and "which ORT did this process load?".

## Symptom → likely cause

| Symptom | Likely cause | Check |
| --- | --- | --- |
| `DmlExecutionProvider` is missing from `GetEpDevices()` | A foreign `onnxruntime.dll` loaded (stock ORT package native assets, a GenAI flavor other than `.WinML`, or a build step copying ORT), or `DirectML.dll` was trimmed | `GetVersionString()` vs the Windows ML table; list DLLs in the output folder |
| A catalog EP is never in the device list | `EnsureReadyAsync` failed silently, `TryRegister` returned false, OS older than 24H2, or unsupported hardware or driver | Log ready results; check `FindAllProviders()` |
| A device was present yesterday and is gone today | Windows Update changed the EP or the driver | `PackageId.Version`, driver version |
| Cold start takes minutes on an NPU/GPU EP | No EP-context cache, or the cache was invalidated | Validate the compiled model; check the cache key |
| A crash or access violation calling a newer ORT C# API | Managed ORT is newer than Windows ML's native ORT | Compare the managed package version with the native `GetVersionString()` |
| GenAI says the provider is unknown | EP not registered in the process env, or the provider name is misspelled or wrongly cased in `AppendProvider`/`SetProviderOption` | Register before `new Model(config)`; match the names exactly |
