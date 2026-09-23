# TensorRT RTX EP ABI plugin

TensorRT RTX is a runtime provider plugin, not a model and not a Windows ML catalog EP.

## Build targets

TRT RTX works on **both** Windows target frameworks because the EP ABI plugin is
plain ONNX Runtime extensibility: it does not need the Windows ML packages:

| Build | TRT RTX | DirectML / WinML catalog EPs |
|-------|---------|------------------------------|
| `net10.0` (portable) | Yes: `NvTensorRTRTXExecutionProvider` via `RegisterExecutionProviderLibrary` | No; unavailable in discovery, CLI warns at parse time |
| `net10.0-windows10.0.19041.0` | Yes | Yes |

On Linux `net10.0` the plugin bundle (`libonnxruntime_providers_nv_tensorrt_rtx.so` +
`libtensorrt_rtx.so` set) registers the same way.

Effective NVIDIA Windows fallback chain: **TRT RTX → DirectML → CPU**. On the
portable build the chain is TRT RTX → CPU, since DirectML requires the
Windows-targeted build.

Trackdub registers the standalone ONNX Runtime EP ABI plugin before creating TRT RTX sessions, then selects the GPU `OrtEpDevice` whose EP name is:

```text
NvTensorRTRTXExecutionProvider
```

Do not accept `NvTensorRtRtxExecutionProvider` as the primary plugin identity. That spelling belonged to the deprecated Windows ML catalog wiring and must not be used for the standalone plugin route.

## Bundle layout

The plugin directory must contain these files together.

Windows:

```text
onnxruntime_providers_nv_tensorrt_rtx.dll
tensorrt_rtx_1_5.dll
tensorrt_onnxparser_rtx_1_5.dll
```

Linux (v0.3.0 cu12 linux-x64 tarball):

```text
libonnxruntime_providers_nv_tensorrt_rtx.so
libtensorrt_rtx.so
libtensorrt_onnxparser_rtx.so
libtensorrt_plugins.so
```

Companion libraries such as `tensorrt_plugins.dll` / `libtensorrt_plugins.so` are copied during install but are not part of the required readiness triple.

## Locator order

`TensorRtRtxPluginLocator` resolves the bundle directory in this order:

1. `StudioSettings.TensorRtRtxPluginDirectory`
2. `TRACKDUB_TRT_RTX_EP_DIR`
3. Default installed bundle under the Trackdub user data root (see below)

If the bundle is missing, use **Install** in Model Manager (downloads v0.3.0 cu12, persists the studio path, then registers), or run the dev fetch script.

## Fetch script (dev/CI)

From repo root on Windows or Linux x64:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
```

Optional `-InstallRoot` or `TRACKDUB_DATA_ROOT` overrides the user data root. The script verifies SHA-256 + size from `runtime/trt-rtx-ep.manifest.json`, extracts native libraries into the flat plugin directory, and prints `TRACKDUB_TRT_RTX_EP_DIR` for the current shell.

## Bundle channel (in-app + manifest)

Trackdub ships the EP ABI plugin through a pinned manifest, not the Windows ML catalog:

- **Manifest:** `runtime/trt-rtx-ep.manifest.json` (version `0.3.0`, CUDA `cu12`, per-RID archive URL + SHA-256 + size).
- **Composition copy:** `trt-rtx-ep.manifest.json` next to the app assembly (`Trackdub.Composition` `CopyToOutputDirectory`).
- **Downloader:** `TrtRtxEpBundleDownloader` verifies checksum/size, extracts required plugin files into `%UserDataRoot%/Providers/trt-rtx/0.3.0/cu12/<rid>/`.
- **In-app install:** Model Manager **Install** calls `ITrtRtxEpInstaller` after `NvidiaTensorRtRtx` license acceptance, persists `StudioSettings.TensorRtRtxPluginDirectory`, then registers via `ITensorRtRtxProviderBootstrap`.
- **Inference bootstrap:** registers an already-installed bundle only; it **never** downloads the EP bundle (same policy as WinML catalog EPs during session bootstrap).

## Download policy (install vs session)

| Path | May download TRT RTX EP bundle? |
|------|--------------------------------|
| Model Manager **Install** / bulk catalog install | Yes (after NVIDIA license acceptance) |
| CLI `trackdub providers trt-rtx install --accept-license` | Yes |
| Inference session bootstrap (`OnnxExecutionSessionFactory`) | **No** |
| Readiness / doctor / `providers trt-rtx status` | **No** |
| Benchmark harness (`BenchmarkTensorRtRtxBootstrap` with `allowProviderDownloads: true`) | Yes, only when license already accepted and bootstrap opts in |

Portable and per-user installs use the in-app or CLI install paths. A future MSI/EXE/MSIX wizard should call the same `ITrtRtxEpInstaller` after license acceptance (see [packaging/installer/README.md](../../packaging/installer/README.md)).

## Registration flow

`TensorRtRtxPluginService` owns plugin registration:

1. Verify NVIDIA hardware eligibility (Windows registry probe or Linux `/proc/driver/nvidia/gpus`).
2. Resolve and validate the plugin bundle directory.
3. Call `OrtEnv.Instance().RegisterExecutionProviderLibrary(...)` for `onnxruntime_providers_nv_tensorrt_rtx.dll`.
4. Enumerate `OrtEnv.Instance().GetEpDevices()`.
5. Require a GPU device named `NvTensorRTRTXExecutionProvider`.
6. Session creation appends that device through `SessionOptions.AppendExecutionProvider(env, devices, options)`.

TRT RTX must not call Windows ML `ExecutionProvider.TryRegister`, `EnsureAndRegisterCertifiedAsync`, or `SessionOptions.SetEpSelectionPolicy`.

## Runtime cache

Provider binaries and runtime cache are separate.

Provider bundle (installed by Model Manager or `Fetch-TrtRtxEp.ps1`):

```text
%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64\   # Windows
~/.local/share/Trackdub/Providers/trt-rtx/0.3.0/cu12/linux-x64/   # Linux
```

Manifest: `runtime/trt-rtx-ep.manifest.json` (pinned NVIDIA GitHub release URLs + checksums).

Compiled TRT RTX runtime cache:

```text
%LOCALAPPDATA%\Trackdub\EngineCache\
```

Clear the engine cache after a GPU driver change, GPU swap, or TRT RTX EP version bump when inference fails with stale compiled engines:

```powershell
trackdub cache clear engines
trackdub doctor   # shows cache path and approximate size
```

Override cache roots:

```powershell
$env:TRACKDUB_ENGINE_CACHE_ROOT = "D:\TrackdubEngineCache"
$env:TRACKDUB_CACHE_ROOT = "D:\TrackdubCache"
```

`TRACKDUB_ENGINE_CACHE_ROOT` wins. Otherwise `TRACKDUB_CACHE_ROOT\EngineCache` is used. Otherwise Trackdub falls back to `%LOCALAPPDATA%\Trackdub\EngineCache`.

## Readiness states

Keep these states separate:

- Plugin directory resolved
- All required DLLs present
- Plugin registered with ORT
- `NvTensorRTRTXExecutionProvider` GPU device visible
- Model files downloaded and checksum verified
- Model/provider pair smoke-tested
- Pipeline stage ran and produced usable artifacts

Provider registration alone is not model readiness. A failed TRT RTX plugin route may fall back to DirectML, but the selected provider must be reported as DirectML, not TRT RTX.

## CLI / SDK execution-provider pins (soft prefer)

`--execution-provider trt-rtx` (and other Kind pins) is a **soft preference**: the planner tries TRT RTX first among stage/engine allow-lists, then falls through (for example DirectML → CPU) when the engine family forbids TRT, smoke fails, or session init fails on unsupported ops.

Hard pin (collapse allow-list / block on smoke-init failure when TRT is allowed):

```powershell
trackdub dub ... --execution-provider trt-rtx --require-execution-provider
```

SDK equivalent: `WithExecutionProvider(ExecutionProviderKind.TensorRTRtx)` soft-prefers; pass `require: true` for a hard pin.

Engine-family allow-lists deny TensorRT families for graphs that hard-fail session init under TRT RTX (examples: `whisper-onnx`, `opus-mt` / `madlad`, `chatterbox`, `cosyvoice`, `qwen3-tts`, `latentsync-diffusion`). Those stages still run under a global `trt-rtx` soft prefer by selecting DirectML/CPU. Do **not** treat this as hybrid VRAM spillover / `supports_partial_offload`; Trackdub does not claim partial offload for TRT RTX.

**ORT GenAI loads are excluded from TensorRT entirely** (`whisper-genai`, `phi-genai`, `qwen-instruct` engine-family overrides). ORT GenAI's `NvTensorRtRtx` device can terminate the host process with a native stack overflow during model init/generation (observed on `qwen-instruct`, Qwen2.5-1.5B). A fatal crash cannot surface as a catchable smoke failure, so the planner never offers TensorRT to GenAI-loaded families and the smoke tester refuses the combination before touching native code.

Session create also retries once with DirectML (Windows) then CPU when TensorRT RTX was selected and init fails with EP/kernel/importer errors (`Kernel not found`, `ModelImporter`, `No graph will run on TensorRT`, etc.), and records that path in `FallbackReason` / bootstrap detail. Pass `--require-execution-provider` / `allowTrtInitFallback: false` to disable that retry on hard-pin routes.

This TRT session-init retry applies to **single-session** factory paths (`CreateSingleAsync` / `CreatePooledSingleAsync`). Multi-session factories (Whisper/Opus/Qwen3-ASR/LatentSync pools) still rely on planner engine-family allow-lists and soft-prefer smoke fallthrough; they do not re-run TRT→DirectML init fallback per role in this pass.

## Quieter TRT capability probe logs

ORT may emit per-node `SkipLayerNormalization` / unsupported-op ERROR spam while probing TRT RTX capability. That spam is ORT/NVIDIA noise, not an install failure. To reduce stderr noise without hiding real session failures:

```powershell
$env:ORT_LOG_SEVERITY_LEVEL = "3"   # 0=Verbose .. 4=Fatal; 3=Error still shows failures, raise to 4 if needed
```

Trackdub does not rewrite ORT's default log severity in session bootstrap.

## Smoke commands

Catalog sweep (planner-style smoke over every cached bundled GPU target; prints
`PASS`/`FAIL`/`SKIP` per target as each completes so a native crash mid-catalog
does not lose earlier results):

```powershell
trackdub providers trt-rtx smoke
```

Readiness/probe slices:

```powershell
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~TensorRtRtxPluginLocator|FullyQualifiedName~OnnxExecutionSessionFactory" --no-restore -m:1
```

Benchmark smoke on a Windows NVIDIA RTX machine with the plugin bundle available:

```powershell
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64"
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider trt-rtx --runs 1 --format console
```

For explicit session testing, inspect the selected provider in benchmark output. Do not infer success from plugin registration logs alone.

## Benchmark / DubBench bootstrap

`Trackdub.Benchmarks` and DubBench share `BenchmarkOnnxExecutionBootstrap`:

- **WinML registry:** `ConfigureExecution(...)` before ONNX runs (same as `Trackdub.Benchmarks Program.cs`).
- **TRT RTX runner factory:** `CreateOnnxRunner()` wires `BenchmarkTensorRtRtxBootstrap` (plugin directory providers + optional license-gated bundle ensure).

Readiness paths (pick one):

1. **Model Manager** — Install after `NvidiaTensorRtRtxLicenseAccepted` in studio settings.
2. **Dev/CI fetch** — `tools/dev/Fetch-TrtRtxEp.ps1` (sets `TRACKDUB_TRT_RTX_EP_DIR`).
3. **Auto-download (opt-in)** — when `NvidiaTensorRtRtxLicenseAccepted` is true in `%LOCALAPPDATA%\Trackdub\settings.json` and benchmark/bootstrap allows provider downloads.

Headless operators:

```powershell
trackdub providers trt-rtx status
trackdub providers trt-rtx install --accept-license
trackdub doctor   # includes tensorrt-rtx-plugin probe row (no download)
```

DubBench ONNX runs call the same bootstrap before each benchmark invocation.

## Bumping TRT RTX EP release

When NVIDIA ships a new `TensorRT-RTX-EP-ABI` GitHub release:

1. Run `tools/dev/Update-TrtRtxEpManifest.ps1 -Version <x.y.z>` to refresh `runtime/trt-rtx-ep.manifest.json` (URLs, SHA-256, size).
2. Update `TensorRtRtxProviderConstants.BundledVersion`, install hints, and default install path segments if the version changed.
3. Run `tools/dev/Fetch-TrtRtxEp.ps1` locally and verify `trackdub providers trt-rtx status`.
4. Run optional GPU smoke (`.github/workflows/trt-rtx-smoke.yml`) or `Trackdub.Benchmarks.DevHost --provider trt-rtx`.
5. Run `trackdub doctor` and advise users with stale engines to `trackdub cache clear engines` after upgrading the EP bundle.

## CI optional GPU tier

Default CI (`ci.yml`) stays unit/fake-backed. Optional smoke workflow: `.github/workflows/trt-rtx-smoke.yml`.

| Gate | Meaning |
|------|---------|
| Repository variable `TRACKDUB_TRT_RTX_SMOKE=true` | Enables the workflow job on self-hosted Windows with NVIDIA RTX |
| `TRACKDUB_TRT_RTX_EP_DIR` | Plugin directory after fetch (workflow sets from default install root) |
| `TRACKDUB_TRT_RTX_SMOKE=1` | Test attribute gate for optional integration tests (`RequiresTrtRtxFactAttribute`) |

The smoke job runs `Fetch-TrtRtxEp.ps1`, exports `TRACKDUB_TRT_RTX_EP_DIR`, then one `Trackdub.Benchmarks.DevHost --provider trt-rtx` invocation. A nonzero smoke exit fails the job.

## References

- [ONNX Runtime TensorRT RTX EP](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)
- [ONNX Runtime plugin EP usage](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)
- [ADR-0002 Windows ML provider strategy](../decisions/ADR-0002-windows-ml-provider-strategy.md)
