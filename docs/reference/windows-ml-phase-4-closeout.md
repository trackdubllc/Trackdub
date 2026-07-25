# Windows ML Phase 4 closeout

Production closeout for Windows ML device policies (Phase 3) and ONNX runtime alignment on Windows.

**Status:** Implementation complete in repo; hardware matrix rows remain manual on Tony PC where noted.

## Workstream A0 — Phase 3 review fixes (pre-merge)

| ID | Item | Status |
|----|------|--------|
| A0.1 | Cache `StudioSettingsWindowsMlEpDevicePolicyProvider` after first load | Done |
| A0.2 | Legacy pool fingerprint for `Explicit` / CPU (no policy key); catalog GPU non-explicit includes policy | Done |
| A0.3 | `TolerantWindowsMlExecutionDevicePolicyJsonConverter` | Done |
| A0.4 | Expanded `ShouldUseCatalogDevicePolicy` tests | Done |
| A0.5 | `ShowsWindowsMlDevicePolicyPanel` on `IDesktopPlatformService` | Done |
| A0.6 | Corrupt settings: log, timestamped `.corrupt` backup, defaults | Done |
| A0.7 | Thread-safe `OnnxExecutionSessionFactory.Initialize` (first call wins) | Done |

## Workstream A — Land Phase 3

- Build: `dotnet build Trackdub.sln`
- Tests: `dotnet test tests/Trackdub.Infrastructure.Tests tests/Trackdub.Inference.Tests`
- Windows TFM (local): `dotnet build Trackdub.sln -f net10.0-windows10.0.19041.0`

## Workstream F — ORT native alignment (P0)

**Problem:** Managed ORT 1.24.x vs WinML-bundled native 1.17.x caused API version 24 errors.

**Mitigation:** Resolver prefers managed-package `runtimes/win-*/native` before app base; benchmarks log managed ORT assembly version.

**Verify:**

```powershell
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <path> --provider trt-rtx --runs 1 --format console
```

## Workstream C — Pool eviction + policy cache

`InferenceSessionPool.EvictAllIdleAsync`, `IInferenceSessionPoolEvictor`, settings-save eviction + cache invalidation.

## Workstream D — Benchmark CLI

`--windows-ml-device-policy explicit|max-performance|prefer-npu|max-efficiency|min-overall-power`

(Additional policies have landed since this Phase 4 closeout; see [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md) for the current, canonical value list.)

Windows ML catalog/device-policy benchmark routes (`dml`, `migraphx`, `auto`) follow the same mutual-exclusion rule as the studio session factory: non-`Explicit` policies call `SetEpSelectionPolicy` only; explicit append runs only when policy is `Explicit`. `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT routes ignore device policy.

## Workstream B — Hardware matrix (manual)

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) after F passes on hardware.

## Workstream E — ADR

[ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md) Phase 4 section.

## Related

- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)
