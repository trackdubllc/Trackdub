# NVIDIA AFX stubs

Trackdub registers **NVIDIA Audio Effects (AFX / RTX Voice)** as a first-class **stubbed** speech-enhancement integration. DeepFilterNet3 remains the shipping enhancement backend.

## Honesty contract

| Signal | Stub behavior |
|--------|----------------|
| `NvidiaAfxIntegration.IsStubbed()` | `true` until native packaging + real download URLs ship |
| `INvidiaAfxRuntimeReadinessService` | DI uses `StubNvidiaAfxRuntimeReadinessService`; always `IsReady=false` |
| `NvidiaAfxRuntimeReadinessService` | Also short-circuits while stubbed (defense in depth) |
| `NvidiaAfxSpeechAudioEnhancementService` | Falls through to DeepFilterNet when stubbed/disabled/not ready |
| `NvidiaAfxRuntimeDownloader` | Throws; refuses placeholder installs |
| `nvidiaafx-runtime.manifest.json` | Placeholder `example.invalid` URLs / zero hashes (not downloadable) |

Never treat component registration, settings fields, or profile catalog entries as proof that AFX ran or succeeded.

## Discoverability

| Area | Location |
|------|----------|
| Provider id / stub flag | `src/Trackdub.Contracts/NvidiaAfxIntegration.cs` |
| Profile enum | `src/Trackdub.Contracts/NvidiaAfxProfile.cs` |
| Studio settings | `StudioSettings.EnableNvidiaAfx` / `NvidiaAfxProfile` / `NvidiaAfxIntensityRatio` |
| Enhancement options | `SpeechAudioEnhancementOptions` |
| Profile catalog | `src/Trackdub.Composition/NvidiaAfx/NvidiaAfxProfileCatalog.cs` |
| Stub readiness | `StubNvidiaAfxRuntimeReadinessService` |
| Future native seams | `NvidiaAfxNative`, `NvidiaAfxSession` |
| Runtime manifest | `src/Trackdub.Composition/nvidiaafx-runtime.manifest.json` |
| DI | `CompositionRoot.AddApplication`, `HeadlessCompositionRoot` |
| Stage provenance | `SpeechAudioEnhancementStageHandler` (`nvidia-afx` backend tag) |

## Still stubbed (not ready for real AFX wiring)

- Real NVIDIA AFX SDK redistributable download URLs, checksums, and license acceptance UX
- Architecture-bucket package selection against a verified install
- Native `NvAudioEffects.dll` load + effect create/run validated on shipping GPUs
- Acoustic echo cancellation far-end reference audio path
- Settings → `SpeechAudioEnhancementOptions` plumbing through the enhancement stage (fields exist; stage currently uses defaults)
- Flipping `NvidiaAfxIntegration.IsStubbed()` to return `false` and registering `NvidiaAfxRuntimeReadinessService` instead of the stub

## Ready for follow-up wiring

- Contracts, profile catalog, DI discovery, honest readiness/download refusal, and stub-contract tests
- Enhancement wrapper that can prefer AFX once readiness is real, with DeepFilterNet fallback
