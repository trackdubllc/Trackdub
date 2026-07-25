# MIGraphX phase 0 — extension points

| Area | Location |
|------|----------|
| Provider enum | `src/Trackdub.Domain/Common/RuntimePlanning.cs` — `ExecutionProviderKind` |
| Milestone provider order | `src/Trackdub.Inference/Runtime/Planning/StageRuntimeRequirements.cs` — `Milestone5PlanningPolicy`, `StageRuntimeRequirementsCatalog` |
| Discovery | `src/Trackdub.Inference.Onnx/Runtime/Planning/OnnxExecutionProviderDiscovery.cs` |
| Bootstrap (platform) | `WindowsExecutionProviderBootstrapper`, `LinuxExecutionProviderBootstrapper` |
| WinML catalog | `WindowsMlExecutionProviderBootstrapper.Windows.cs`, `WindowsMlProviderRegistrationPolicy.cs` |
| Session options | `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs` — `CreateSessionOptions` |
| Smoke tests | `OnnxExecutionProviderSmokeTester.cs` |
| Devices | `WindowsDeviceEnumerator.cs`, `LinuxDeviceEnumerator.cs` |
| Studio hardware overrides | `HardwareOverrideCatalog.cs`, `IStudioSettingsService.HardwareOverrides` |
| DI | `CompositionRoot.AddInference` |
| Strategy doc | [ADR-0002-windows-ml-provider-strategy.md](../adr/ADR-0002-windows-ml-provider-strategy.md) |
