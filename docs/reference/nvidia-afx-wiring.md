# NVIDIA AFX wiring status

Trackdub public core AFX integration is past pure stubs for **API surface and plumbing**, but still **not ready** for shipping AFX enhancement. `NvidiaAfxIntegration.IsStubbed()` remains `true`.

## What works now

| Area | Status |
|------|--------|
| Packaging gates | Placeholder `example.invalid` / zero-hash packages are rejected by `NvidiaAfxRuntimePackageGates` |
| License UX field | `StudioSettings.NvidiaAfxLicenseAccepted` + installer refuses without acceptance |
| Local runtime override | `NvidiaAfxRuntimeDirectory` / `TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT` resolved by `NvidiaAfxRuntimePathResolver` |
| Installer scaffolding | `NvidiaAfxRuntimeInstaller` (stub + gate + license) registered in DI |
| Native P/Invoke | Aligns with Maxine `nvAudioEffects.h` (`float**` `NvAFX_Run`, sample/channel params) |
| AEC far-end | Options + session accept near-end + far-end; stage request can pass `FarEndReferenceAudioPath` |
| Settings → stage | `SpeechAudioEnhancementStageHandler` loads `SpeechAudioEnhancementOptions.FromStudioSettings` |
| DeepFilterNet fallback | Still the live enhancement path while stubbed / not ready |

## Still blocked (honest)

- Trackdub-hosted Maxine AFX redistributable ZIP URLs + verified SHA-256/size (NVIDIA ships via developer installer, not a public CDN like TensorRT-RTX EP)
- Flipping `IsStubbed()` / registering real readiness in DI
- Verified `NvAudioEffects.dll` create/load/run on Turing+ GPU with Tensor Cores
- End-to-end AEC with a real far-end reference in a project pipeline on Windows+NVIDIA
- Desktop UI for AFX license accept / runtime directory (contracts are ready; gated app UI is out of this PR)

## Local probing (after stub flip only)

1. Install Maxine AFX from [NVIDIA broadcast SDK resources](https://www.nvidia.com/broadcast-sdk-resources).
2. Point `NvidiaAfxRuntimeDirectory` or `TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT` at the install root that contains `NvAudioEffects.dll` and `models/`.
3. Accept `NvidiaAfxLicenseAccepted` before any Trackdub download path is used.
4. Only then flip `IsStubbed()` and swap DI from `StubNvidiaAfxRuntimeReadinessService` to `NvidiaAfxRuntimeReadinessService`.

Repo reference: `docs/reference/nvidia-afx-stubs.md` (stub contract) and this wiring status.
