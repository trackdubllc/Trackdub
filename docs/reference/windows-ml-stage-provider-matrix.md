# Windows ML stage provider matrix (Phase 2)

Internal audit companion for [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md) stage catalog alignment.

## Planner intersection

`RuntimePlanFactory.GetOrderedProviders` orders providers as:

`Milestone5PlanningPolicy.SupportedProvidersThisMilestone` ∩ `AllowedProvidersThisMilestone` (or engine-family override).

Phase 2+ sets default stage allow-lists to `StageRuntimeRequirementsCatalog.DefaultOnnxStageAllowedProviders` (same sequence as milestone probe order) for ONNX spine stages. **`kokoro` engine-family override remains CPU-only** (ConvTranspose mechanical block).

Milestone probe order (2026-06): `TensorRTRtx` → `Migraphx` → `OpenVinoCatalog` → `Qnn` → `VitisAi` → `TensorRt` → `Cuda` → `OpenVino` → `DirectMl` → `Cpu`. Catalog EPs are in the intersection list; discovery + smoke-test gating still determine whether a route becomes `Verified`.

## Stage defaults (current)

|Stage|Default allow-list|Engine-family overrides|
|-|-|-|
|VAD|Milestone default|—|
|ASR|Milestone default|`whisper-genai` keeps GenAI-oriented list|
|Translation|Milestone default|`madlad`, `phi-genai`|
|Diarization|Milestone default|—|
|Separation|Milestone default|`spleeter`|
|OverlapRescue|Milestone default|`sepformer`|
|SpeechEnhancement|Milestone default|`deepfilternet3`|
|LipSync|Milestone default|`onnx-ctc-phoneme-aligner`|
|LipSynthesis|Milestone default|`latentsync-diffusion`|
|TextRefinement|Milestone default|—|
|TTS|Milestone default|**`kokoro` → CPU only** (ConvTranspose / DirectML incompatible)|

## Windows manual smoke checklist

Run on a machine with the relevant catalog EP installed before claiming GPU readiness in release notes. Record pass/fail in the PR or issue; planner must fall through on smoke failure (no fake readiness).

|Stage|Representative model|Catalog EP to exercise|Kokoro / CPU guard|
|-|-|-|-|
|VAD|`silero-vad`|TensorRT RTX (NVIDIA) or MIGraphX (AMD)|—|
|Diarization|Sortformer 4spk v2.1|Same|—|
|Translation|Any bundled `opus-*` ONNX pair|Same|—|
|TTS|`chatterbox-*`|Same|**Kokoro plan must stay CPU**|
|ASR|`whisper-tiny-onnx`|CUDA / DirectML / catalog per discovery|GenAI path separate|

### Windows smoke (Tony PC, RTX 5080, 2026-05-23)

|Stage|Result|Actual EP|
|-|-|-|
|VAD|pass|tensorrt-rtx (prior smoke); benchmark `dml` pass 2026-05-23|
|Diarization|pass|tensorrt-rtx|
|Translation|N/A|opus ONNX pair not present under `models/` on benchmark host (manifest aliases only)|
|TTS chatterbox|pass|tensorrt-rtx|
|Kokoro CPU guard|pass|cpu|
|ASR|pass|directml|

Suggested commands:

```powershell
dotnet build Trackdub.sln
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~RuntimePlanner"
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --help
```

## Manifest `expected_runtime` (Phase 2)

Bundled ONNX models use canonical token:

`windows-ml|onnxruntime-migraphx|onnxruntime-directml`

Legacy token `onnxruntime-directml|onnxruntime-migraphx` remains parseable in `ModelExpectedRuntimeFormatter` for older manifests.

This field is **governance / Model Manager hints only**; the runtime planner does not read `expected_runtime`.

## Device policy mode smoke (Phase 3)

Set **Settings → Windows ML device policy** to each non-default value, **restart Trackdub**, then run one stage per policy. Harness shortcut: `Trackdub.Benchmarks --model silero-vad --provider dml --windows-ml-device-policy <name>` (catalog GPU; policy mode uses `SetEpSelectionPolicy`).

| Policy | Stage exercised | Pass/fail | Actual EP | Notes |
|--------|-----------------|-----------|-----------|-------|
| Explicit | silero-vad (benchmark) | pass | dml | Explicit append path |
| MaxPerformance | silero-vad (benchmark) | pending | dml/migraphx catalog route only | TRT RTX is no longer selected by Windows ML device policy; use the TRT RTX plugin smoke commands in `tensorrt-rtx-ep-abi-plugin.md`. |
| PreferNpu | silero-vad (benchmark) | pass | dml | No NPU on host; ORT fell back to DML |
| MaxEfficiency | silero-vad (benchmark) | pass | dml | |
| MinOverallPower | silero-vad (benchmark) | pass | dml | |

See [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md).

## TensorRT RTX EP ABI plugin smoke (separate from Windows ML policy)

TRT RTX is **not** a Windows ML catalog EP and is **not** selected by `WindowsMlExecutionDevicePolicy` (including `MaxPerformance`). Validate it with the standalone plugin route only.

| Stage | Representative model | Command / surface | Pass/fail | Actual EP | Notes |
|-------|---------------------|-------------------|-----------|-----------|-------|
| VAD | `onnx-community/silero-vad` | `Trackdub.Benchmarks --provider trt-rtx` | pending | *pending local GPU run* | Requires NVIDIA GPU + plugin bundle |
| Headless status | — | `trackdub providers trt-rtx status` | pending | JSON `isOrtProviderListed` | Probe-only; no download |
| Headless install | — | `trackdub providers trt-rtx install --accept-license` | pending | — | License-gated bundle download |
| DubBench | same as benchmark | DubBench ONNX run after shared bootstrap | pending | — | Uses `BenchmarkOnnxExecutionBootstrap` |

Prerequisites: [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md) (Model Manager, `Fetch-TrtRtxEp.ps1`, or license-accepted auto-download). Optional CI: `.github/workflows/trt-rtx-smoke.yml` when repository variable `TRACKDUB_TRT_RTX_SMOKE=true`.

Suggested smoke:

```powershell
.\tools\dev\Fetch-TrtRtxEp.ps1
$env:TRACKDUB_TRT_RTX_EP_DIR = "$env:LOCALAPPDATA\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64"
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model onnx-community/silero-vad --provider trt-rtx --runs 1 --format console
trackdub providers trt-rtx status
```

## Phase 4 verification (2026-05-23)

Automated closeout: ORT native resolver prefers managed-package `runtimes/win-*/native` before app base; benchmark CLI `--windows-ml-device-policy`; pool eviction on settings save. See [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md).

## Phase 5 catalog EP stubs (2026-05-23)

OpenVINO catalog + QNN + VitisAI documented in ADR-0002 Phase 5 and [windows-ml-phase-5-catalog-eps.md](windows-ml-phase-5-catalog-eps.md). They are included in `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`; discovery may still report unavailable until installed, and smoke failure falls through to the next provider.
