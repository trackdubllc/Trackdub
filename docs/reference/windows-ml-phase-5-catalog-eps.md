# Windows ML Phase 5: catalog EP expansion (OpenVINO, QNN)

Internal checklist for Intel OpenVINO, Qualcomm QNN, AMD MIGraphX, and AMD VitisAI **Windows ML catalog** routes. TensorRT RTX is not a Windows ML catalog route anymore; it uses the standalone ORT EP ABI plugin documented in [tensorrt-rtx-ep-abi-plugin.md](tensorrt-rtx-ep-abi-plugin.md).

## Relationship to existing paths

| Path | Enum | When used |
|------|------|-----------|
| Standalone OpenVINO | `ExecutionProviderKind.OpenVino` | Linux; optional Windows install via component downloader (`Infrastructure`). |
| WinML catalog OpenVINO (stub) | `ExecutionProviderKind.OpenVinoCatalog` | Future Windows catalog append; **not** the same as standalone OpenVINO. |
| WinML catalog QNN (stub) | `ExecutionProviderKind.Qnn` | Future Snapdragon / NPU catalog EP on Windows. |

Do not duplicate “GPU ready” semantics between standalone OpenVINO install state and catalog registration.

## Code seams

| Concern | Location |
|---------|----------|
| Catalog provider name constants | `Trackdub.Inference.Onnx/WindowsMl/WindowsMlCatalogProviderIds.cs` |
| Registration / bootstrap | `WindowsMlProviderRegistrationPolicy.cs`, `WindowsMlExecutionProviderBootstrapper.Windows.cs` |
| Discovery | `OnnxExecutionProviderDiscovery.cs` — stub kinds return **unavailable** |
| Session append | `OnnxExecutionSessionFactory.cs` — `NotSupportedException` until smoke path exists |
| Milestone probe order | `StageRuntimeRequirements.cs` — **unchanged in 5c** |

Stub marker: `#TODO(phase-5-catalog-ep)` in code; reference this doc and [ADR-0002 Phase 5](../adr/ADR-0002-windows-ml-provider-strategy.md).

## Suggested smoke commands (when hardware exists)

Windows TFM:

```powershell
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --help
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <model-id> --provider dml
dotnet run --project src/Trackdub.Benchmarks -f net10.0-windows10.0.19041.0 -- --model <model-id> --windows-ml-device-policy PreferNpu
```

When QNN / catalog OpenVINO CLI aliases exist, add matrix rows here. For TRT RTX smoke commands, use the plugin doc instead of this catalog checklist.

## Matrix rows to add (when hardware exists)

| Hardware | Stage | Catalog EP to exercise | Notes |
|----------|-------|------------------------|-------|
| Intel GPU box | VAD / ASR | OpenVINO catalog | Distinct from standalone OpenVINO row |
| Snapdragon / Copilot+ PC | VAD / ASR | QNN | Pair with `PreferNpu` policy smoke; mark N/A if no NPU |

Update [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md) with pass/fail and **actual EP** from console — never infer from policy name alone.

## Enablement order (post-5c)

1. Confirm catalog ids in `WindowsMlCatalogProviderIds`.
2. Implement session append + smoke tester path.
3. Run stage matrix smoke on target hardware.
4. Update manifest `expected_runtime` and stage allow-lists if product commits.
5. Only then extend `Milestone5PlanningPolicy.SupportedProvidersThisMilestone`.

## References

- [ADR-0002](../adr/ADR-0002-windows-ml-provider-strategy.md)
- [windows-ml-phase-3-device-policies.md](windows-ml-phase-3-device-policies.md)
- [windows-ml-phase-4-closeout.md](windows-ml-phase-4-closeout.md)
- [windows-ml-stage-provider-matrix.md](windows-ml-stage-provider-matrix.md)
