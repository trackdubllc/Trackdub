# NVIDIA AFX wiring status

Trackdub public core AFX integration is past pure stubs for **API surface and plumbing**, but still **not ready** for shipping AFX enhancement. `NvidiaAfxIntegration.IsStubbed()` remains `true`.

## Maxine 3.x runtime layout (implemented)

Runtime root (`NvidiaAfxRuntimeDirectory` / `TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT` / ComponentStore install):

```text
<runtime-root>/
  NVAudioEffects.dll                 # also accepts NvAudioEffects.dll
  external/                          # bundled CUDA/TRT deps from SDK
  features/
    nvafxdenoiser/
      bin/nvafxdenoiser.dll
      models/<turing|ampere|ada|blackwell>/denoiser_*.trtpkg
    nvafxdereverb/
    nvafxdereverbdenoiser/
    nvafxsuperres/
  features-download-manifest.json    # NGC download receipt (publish gate)
```

Resolution is in `NvidiaAfxRuntimeLayout` (Infrastructure). Legacy flat `models/<stem>.nvam` is still accepted as a fallback for older staged trees.

Model requirements are feature+stem pairs on `NvidiaAfxProfileDefinition.RequiredModels`, not hard-coded `.nvam` paths.

**AEC:** Maxine AFX 3.x public selectors do not list `aec`. The AcousticEchoCancellation profile remains discoverable; model/feature resolution will fail until NVIDIA ships a matching package or we drop the profile with evidence.

## What works now

| Area | Status |
|------|--------|
| Packaging gates | Placeholder `example.invalid` / zero-hash / non-hex SHA-256 packages rejected |
| License UX field | `StudioSettings.NvidiaAfxLicenseAccepted` + installer refuses without acceptance |
| Local runtime override | Settings / env / ComponentStore via `NvidiaAfxRuntimePathResolver` |
| Installer scaffolding | Stub seam + license/manifest/download gates; DI registered |
| Native P/Invoke | Maxine `float**` `NvAFX_Run`, exact 1/2 input channels, rate-change trim |
| Native probe before Ready | `INvidiaAfxEffectProbe` create/load after DLL+model presence |
| Settings → stage | Studio settings map into enhancement options (incl. headless preserve) |
| DeepFilterNet fallback | Live enhancement path while stubbed / not ready |

## Still blocked (honest)

- Trackdub-hosted Maxine AFX redistributable ZIP URLs + verified SHA-256/size (NGC/installer, not a public CDN)
- Flipping `IsStubbed()` / registering real readiness in DI
- Verified `NVAudioEffects.dll` create/load/run on Turing+ GPU with Tensor Cores
- End-to-end AEC with a real far-end reference (selector not in Maxine 3.x public docs)
- Desktop UI for AFX license accept / runtime directory

## Local probing (after stub flip only)

1. Install Maxine AFX core ZIP; run `features/download_features.ps1` for the target GPU arch.
2. Point `NvidiaAfxRuntimeDirectory` or `TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT` at the SDK root.
3. Accept `NvidiaAfxLicenseAccepted` before any Trackdub download path is used.
4. Prove create/load/run on Windows+NVIDIA, then flip `IsStubbed()` and swap DI from `StubNvidiaAfxRuntimeReadinessService` to `NvidiaAfxRuntimeReadinessService`.

Repo reference: `docs/reference/nvidia-afx-stubs.md`.
