# NVIDIA AFX wiring status

NVIDIA AFX works end to end for a **locally installed** runtime. `NvidiaAfxIntegration.IsStubbed()` is `false` and only acts as a kill switch; readiness is decided by probing the native runtime. Trackdub does not host the AFX redistributables, so the installer still refuses and users point Trackdub at their own SDK.

## Verified

On an RTX 5070 (Blackwell) with NVIDIA Audio Effects SDK **2.1.0.9** and the `nvidia/maxine/afx_win_*:2.1.0-*` Blackwell models from NGC, every selectable profile creates, loads and processes audio:

| Profile | Selector | Rates |
|---------|----------|-------|
| Noise Removal | `denoiser` | 16 kHz, 48 kHz |
| Reverb Removal | `dereverb` | 16 kHz, 48 kHz |
| Noise + Reverb Removal | `dereverb_denoiser` | 16 kHz, 48 kHz |
| Telephony Upscale | `superres8kto16k_denoiser16k` (chained) | 8 kHz in, 16 kHz out |

The same-rate profiles also run on the flat SDK 1.6.1.2 layout. Acoustic Echo Cancellation is not covered (needs a far-end reference).

Run the live proofs (skipped unless the variable is set):

```powershell
$env:TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT = "<SDK root>"
dotnet test tests/Trackdub.Composition.Tests -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~NvidiaAfxLiveRuntime"
```

## Runtime layout

The runtime root is the SDK root (`NvidiaAfxRuntimeDirectory` setting, `TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT`, or a component-store install):

```text
<runtime-root>/
  bin/NVAudioEffects.dll             # SDK 2.x; flat installs keep it at the root
  bin/external/{cuda,nvtrt,openssl}/bin/*.dll
  features/
    nvafxdenoiser/        bin/nvafxdenoiser.dll        models/<turing|ampere|ada|blackwell>/denoiser_{16k,48k}.trtpkg
    nvafxdereverb/        bin/nvafxdereverb.dll        models/<arch>/dereverb_{16k,48k}.trtpkg
    nvafxdereverbdenoiser/ bin/nvafxdereverbdenoiser.dll models/<arch>/dereverb_denoiser_{16k,48k}.trtpkg
    nvafxsuperres/        bin/nvafxsuperres.dll        models/<arch>/superres_8kto16k.trtpkg
```

Resolution is in `NvidiaAfxRuntimeLayout`. A flat `models/<stem>.trtpkg` (SDK 1.6) or legacy `.nvam` tree is still accepted.

Get the features with `features/download_features.ps1` (needs an NGC API key) or the NGC CLI, one package per variant:

```text
ngc registry model download-version nvidia/maxine/afx_win_denoiser:2.1.0-48k-blackwell --dest <dir>
ngc registry model download-version nvidia/maxine/afx_win_denoiser:2.1.0-dynamic-library --dest <dir>
```

Place model files under `features/nvafx<effect>/models/<arch>/` and the DLL under `features/nvafx<effect>/bin/`. Readiness requires the models for every supported rate of the selected profile.

## Native behavior worth knowing

- The core DLL loads on its own, but feature DLLs depend on CUDA, TensorRT and OpenSSL under `bin/external`. Those folders are not on the DLL search path, so `NvidiaAfxNativeLoader` preloads them (in repeated passes, because they depend on each other) before the feature DLLs. Without that, `CreateEffect` returns `NVAFX_STATUS_LIBRARY_ERROR` (20).
- Chained effects reject `input_sample_rate` (their rates are encoded in the selector) and the scalar `intensity_ratio` setter (`NVAFX_STATUS_INVALID_PARAM`). Intensity goes through `NvAFX_SetFloatList` with one ratio per effect; Trackdub keeps super-resolution at 1.0 and applies the user's ratio to the denoiser. Every `Get*` call on a chained handle returns `NVAFX_STATUS_FAILED`, and the SDK documents only a 10 ms frame for chains, so Trackdub derives frame sizes from the sample rates (80 / 160 / 480 samples at 8 / 16 / 48 kHz).
- Frame-size parameters are `num_samples_per_input_frame` / `num_samples_per_output_frame`; the older `num_samples_per_frame` is the fallback for SDK 1.x.
- The GPU architecture bucket comes from the display adapter, not a default. No NVIDIA adapter reports `unsupported`.

## Still open

- A Trackdub-hosted redistributable (license-dependent); until then the runtime is user-installed.
- End-to-end AEC with a real far-end reference.
- Speaker Focus (Early Access) and Studio Voice are in the SDK but not in the profile catalog.
- No GPU CI tier.

See [nvidia-afx-stubs.md](nvidia-afx-stubs.md) for the readiness contract.
