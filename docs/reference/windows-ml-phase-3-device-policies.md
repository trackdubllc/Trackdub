# Windows ML Phase 3: device policies

Optional advanced studio setting for catalog GPU sessions on Windows.

## Setting

- **Key:** `StudioSettings.WindowsMlExecutionDevicePolicy` (JSON enum name)
- **Default:** `Explicit` (unchanged production behavior)
- **UI:** Settings → Hardware → *Windows ML device policy (advanced)* (Windows only)
- **Restart:** Policy changes take effect on new sessions after save; Phase 4 evicts idle pooled sessions and invalidates the policy provider cache (restart still recommended if a leased session holds old options).

## Values

| Setting | ORT `ExecutionProviderDevicePolicy` | Behavior |
|---------|-------------------------------------|----------|
| `Explicit` | *(none)* | `GetEpDevices()` + `AppendExecutionProvider` (Phase 1–2 path) |
| `MaxPerformance` | `MAX_PERFORMANCE` | `SetEpSelectionPolicy` only; no per-EP append |
| `PreferNpu` | `PREFER_NPU` | Same |
| `MaxEfficiency` | `MAX_EFFICIENCY` | Same |
| `MinOverallPower` | `MIN_OVERALL_POWER` | Same |
| `DefaultRender` | `DEFAULT_RENDER` | Same, gated by capability probe (see below) |
| `MinPower` | `MIN_POWER` | Same, gated by capability probe (see below) |

Mapping: `WindowsMlExecutionDevicePolicyMapper` in `Trackdub.Inference.Onnx` (`#if WINDOWS`).

**Capability probe:** `DefaultRender` / `MinPower` resolve `DEFAULT_RENDER` / `MIN_POWER` by name via `Enum.TryParse` against the managed `Microsoft.ML.OnnxRuntime.ExecutionProviderDevicePolicy` surface, probed once per process. The pinned managed package version does not reliably predict when these members appear, so the probe requires **both** names to be present; if either is missing, `ApplyIfNeeded` returns early without calling `SetEpSelectionPolicy` (same as `Explicit`) instead of throwing.

## Rules

1. **Mutual exclusion:** Per session, either policy mode **or** explicit append — never both.
2. **Catalog only:** Policy applies to Windows ML catalog routes (`DirectMl`, `MIGraphX`). It does **not** select TensorRT RTX — TRT RTX uses the standalone EP ABI plugin (`trackdub providers trt-rtx`, Model Manager Install, or `Fetch-TrtRtxEp.ps1`).
3. **CPU / Kokoro:** CPU sessions never set policy; Kokoro CPU-only override unchanged.
4. **Planner / smoke:** Unchanged — `RuntimePlanFactory` and `OnnxExecutionProviderSmokeTester` still gate readiness.
5. **Fingerprint:** `BuildSessionOptionsFingerprint` includes policy key so pooled sessions do not mix explicit vs policy options.

## Seams

- Contracts: `WindowsMlEpDevicePolicyContracts.cs`, `IWindowsMlEpDevicePolicyProvider`
- Settings: `JsonStudioSettingsService.Normalize` coerces unknown enum to `Explicit`
- Composition: `StudioSettingsWindowsMlEpDevicePolicyProvider` → `OnnxExecutionSessionFactory.Initialize(bootstrapper, policyProvider)`
- Session factory: `OnnxExecutionSessionFactory.CreateSessionOptions(provider, devicePolicy, …)`

## Manual validation

After explicit matrix baseline on hardware:

1. `Explicit` — no regression vs Phase 2.
2. `MaxPerformance` — one VAD or ASR run on a **catalog** EP (`dml` / `migraphx`); log actual EP from benchmark or session metadata. Do not use this step to validate TRT RTX (see TRT RTX plugin smoke table in the stage matrix).
3. `PreferNpu` / `MaxEfficiency` — on Copilot+ PC if available; else N/A in matrix.
4. Change policy → restart → confirm new fingerprint / sessions.

**Benchmark harness:** `Trackdub.Benchmarks --windows-ml-device-policy <name>` configures `OnnxModelBenchmarkRunner`. For Windows ML catalog/device-policy routes (`dml`, `migraphx`, `auto`), non-`Explicit` policies use `SetEpSelectionPolicy` only (no explicit catalog-device append). `trt-rtx` uses the standalone EP ABI plugin and ignores Windows ML device policy. CPU and native CUDA/TensorRT benchmark routes also never apply device policy.

## References

- [Select execution providers (device policies)](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers)
- [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md)
