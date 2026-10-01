# NVIDIA AFX stubs

Trackdub registers **NVIDIA Audio Effects (AFX / RTX Voice)** as a first-class **stubbed** speech-enhancement integration for readiness. DeepFilterNet3 remains the shipping enhancement backend. Wiring beyond stubs (native API alignment, packaging gates, AEC far-end, settings→stage) is documented in [nvidia-afx-wiring.md](nvidia-afx-wiring.md).

## Honesty contract

| Signal | Stub behavior |
|--------|----------------|
| `NvidiaAfxIntegration.IsStubbed()` | `true` until Trackdub-hosted redistributables + verified native create/run ship |
| `INvidiaAfxRuntimeReadinessService` | DI uses `StubNvidiaAfxRuntimeReadinessService`; always `IsReady=false` |
| `NvidiaAfxRuntimeReadinessService` | Also short-circuits while stubbed (defense in depth) |
| `NvidiaAfxSpeechAudioEnhancementService` | Falls through to DeepFilterNet when stubbed/disabled/not ready/missing AEC far-end |
| `NvidiaAfxRuntimeDownloader` / `NvidiaAfxRuntimeInstaller` | Refuse while stubbed; also refuse placeholder packages and missing license acceptance |
| `nvidiaafx-runtime.manifest.json` | Placeholder `example.invalid` URLs / zero hashes (not downloadable; gated by `NvidiaAfxRuntimePackageGates`) |

Never treat component registration, settings fields, or profile catalog entries as proof that AFX ran or succeeded.

## Discoverability

| Area | Location |
|------|----------|
| Provider id / stub flag | `src/Trackdub.Contracts/NvidiaAfxIntegration.cs` |
| Profile enum | `src/Trackdub.Contracts/NvidiaAfxProfile.cs` |
| Studio settings | `EnableNvidiaAfx` / `NvidiaAfxProfile` / `NvidiaAfxIntensityRatio` / `NvidiaAfxLicenseAccepted` / `NvidiaAfxRuntimeDirectory` |
| Enhancement options | `SpeechAudioEnhancementOptions` (+ `FarEndReferenceAudioPath`, `FromStudioSettings`) |
| Profile catalog | `src/Trackdub.Composition/NvidiaAfx/NvidiaAfxProfileCatalog.cs` |
| Stub readiness | `StubNvidiaAfxRuntimeReadinessService` |
| Native seams | `NvidiaAfxNative`, `NvidiaAfxSession` (Maxine `float**` Run + AEC dual input) |
| On-disk layout | `NvidiaAfxRuntimeLayout` (Maxine 3.x `NVAudioEffects.dll` + `features/nvafx*/`; legacy `models/*.nvam` fallback) |
| Packaging gates | `NvidiaAfxRuntimePackageGates`, `NvidiaAfxRuntimePathResolver`, `NvidiaAfxRuntimeInstaller` |
| Runtime manifest | `src/Trackdub.Composition/nvidiaafx-runtime.manifest.json` |
| DI | `CompositionRoot.AddApplication`, `HeadlessCompositionRoot` |
| Stage provenance | `SpeechAudioEnhancementStageHandler` (`nvidia-afx` backend tag; settings→options) |

## Still stubbed (blocked for Ready)

- Real NVIDIA AFX SDK redistributable download URLs, checksums (NVIDIA installer / Trackdub-hosted packages)
- Architecture-bucket package selection against a verified install with live download
- Native `NVAudioEffects.dll` create/run validated on shipping GPUs
- Flipping `NvidiaAfxIntegration.IsStubbed()` and registering `NvidiaAfxRuntimeReadinessService` instead of the stub
- AEC: not in Maxine AFX 3.x public selectors; profile stays discoverable only

## Wired but gated

- Packaging validation gates and license-gated installer scaffolding
- Maxine 3.x `features/` layout resolution (+ legacy models fallback)
- Rate-specific Maxine models (`ModelsBySampleRate` / `ResolveRequiredModels`) + feature bin DLL gates
- Native DllImport resolver for `NVAudioEffects.dll` + managed preload of `features/*/bin/*.dll`
- Corrected native Run signature and AEC far-end reference path
- Settings → `SpeechAudioEnhancementOptions` through the enhancement stage
