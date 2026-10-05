# NVIDIA AFX wiring status

NVIDIA AFX works end to end for a **locally installed** runtime. `NvidiaAfxIntegration.IsStubbed()` is `false` and only acts as a kill switch; readiness is decided by probing the native runtime. Trackdub does not host the AFX redistributables, so the installer still refuses and users point Trackdub at their own SDK.

## Verified

On an RTX 5070 (Blackwell), every profile selectable in the UI (all but Acoustic Echo Cancellation, which needs a far-end reference dubbing never has, and the Early Access Speaker Focus) creates, loads and processes audio with NVIDIA Audio Effects SDK **3.0.0.51** (the current release) and its 3.0.0 Blackwell models from NGC, and also with SDK **2.1.0.9** and the 2.1.0 models. Speaker Focus is covered separately below. Feature versions must match the core SDK version. The same code runs on both with no version switches:

| Profile | Selector | Rates |
|---------|----------|-------|
| Noise Removal | `denoiser` | 16 kHz, 48 kHz |
| Reverb Removal | `dereverb` | 16 kHz, 48 kHz |
| Noise + Reverb Removal | `dereverb_denoiser` | 16 kHz, 48 kHz |
| Telephony Upscale | `superres8kto16k_denoiser16k` (chained) | 8 kHz in, 16 kHz out |
| Speaker Focus (Early Access) | `speaker_focus` | 16 kHz, 48 kHz |

Speaker Focus is an Early Access effect, so no test results for it are published here; use SDK 3.0.0 with it. The same-rate profiles other than Speaker Focus also run on the flat SDK 1.6.1.2 layout. Acoustic Echo Cancellation is not covered (needs a far-end reference).

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
    nvafxspeakerfocus/    bin/nvafxspeakerfocus.dll    models/<arch>/speaker_focus_{16k,48k}.trtpkg   (Early Access)
```

Resolution is in `NvidiaAfxRuntimeLayout`. A flat `models/<stem>.trtpkg` (SDK 1.6) or legacy `.nvam` tree is still accepted.

Get the core SDK and the features from NGC (org `nvidia`, team `maxine`). `features/download_features.ps1` needs an NGC API key; the NGC CLI works with an already authenticated session and one package per variant:

```text
ngc registry resource download-version nvidia/maxine/maxine_windows_audio_effects_sdk:3.0.0 --dest <dir>   # x64 core zip; 3.0.0_woa is Windows on Arm
ngc registry model download-version nvidia/maxine/afx_win_denoiser:3.0.0_models_48k_blackwell --dest <dir>
ngc registry model download-version nvidia/maxine/afx_win_denoiser:3.0.0_dynamic_library --dest <dir>
```

NGC variant names changed between releases: 2.1.0 uses `2.1.0-48k-blackwell` and `2.1.0-dynamic-library`, 3.0.0 uses `3.0.0_models_48k_blackwell` and `3.0.0_dynamic_library`. The model file names are the same in both.

Place model files under `features/nvafx<effect>/models/<arch>/` and the DLL under `features/nvafx<effect>/bin/`. Readiness requires the models for every supported rate of the selected profile.

## Native behavior worth knowing

- The core DLL loads on its own, but feature DLLs depend on CUDA, TensorRT and OpenSSL under `bin/external`. Those folders are not on the DLL search path, so `NvidiaAfxNativeLoader` preloads them (in repeated passes, because they depend on each other) before the feature DLLs. Without that, `CreateEffect` returns `NVAFX_STATUS_LIBRARY_ERROR` (20).
- Chained effects reject `input_sample_rate` (their rates are encoded in the selector) and the scalar `intensity_ratio` setter (`NVAFX_STATUS_INVALID_PARAM`). The documented `NvAFX_SetFloatList` (one ratio per effect) is accepted but leaves the output unchanged on Windows (SDK 3.0.0, every list layout tried), so Telephony Upscale has no intensity control. The live `Intensity_changes_the_output...` test guards every profile that advertises intensity. Every `Get*` call on a chained handle returns `NVAFX_STATUS_FAILED`, and the SDK documents only a 10 ms frame for chains, so Trackdub derives frame sizes from the sample rates (80 / 160 / 480 samples at 8 / 16 / 48 kHz).
- Frame-size parameters are `num_samples_per_input_frame` / `num_samples_per_output_frame`; the older `num_samples_per_frame` is the fallback for SDK 1.x.
- The loader keeps one runtime per process. Native libraries cannot be unloaded safely, so pointing Trackdub at a different SDK folder after one has loaded reports "Restart Trackdub to use ..." instead of mixing the first SDK's binaries with the second folder's models. A runtime whose libraries fail to load is not marked loaded, so fixing the folder works without a restart.
- The SDK's CUDA, TensorRT and OpenSSL copies are mapped into the Trackdub process and then serve every later load of those file names. If a different copy of one of them is already loaded (for example ONNX Runtime's), AFX is not started and readiness explains why. The reverse order is not detected: if AFX loads first, a later same-named load elsewhere in the process gets the SDK's copy. Isolating AFX in its own process would remove that risk and is not done here.
- Using AFX requires `NvidiaAfxLicenseAccepted`; readiness reports "License not accepted" until it is set. Unreadable settings and a throwing readiness check fall back to DeepFilterNet instead of failing the stage.
- Architecture candidates come from every NVIDIA display adapter with Tensor Cores (GTX cards are excluded), newest first, because the registry can list stale adapters and does not say which GPU is CUDA device 0. Readiness tries each candidate until its models exist and the native probe passes at every supported rate. `TRACKDUB_NVIDIA_GPU_NAME` replaces detection with a single candidate, and an unrecognised name there falls back to turing. With no NVIDIA adapter the result is `unsupported`.
- Only successful native probes are cached, keyed by runtime folder, effect, sample rate, architecture and the native library's write time. File and model checks run on every call, so a removed model or a GPU change is noticed immediately.

## Still open

- A Trackdub-hosted redistributable (license-dependent); until then the runtime is user-installed.
- Multi-GPU machines are handled by trying each architecture candidate, but a mixed-generation machine has not been verified on hardware.
- End-to-end AEC with a real far-end reference.
- Speaker Focus is an NVIDIA Early Access effect under NVIDIA's evaluation license, so it is licensed for internal testing and evaluation only, not for production use, and must not be shipped to end users unless NVIDIA grants that in writing. Studio Voice is in the SDK but not in the profile catalog. NVIDIA's 3.0.0 docs mark Acoustic Echo Cancellation and Voice Font as deprecated, so they are not planned.
- Denoiser v2 (`effect_version`) is an experimental preview that cannot be chained and enables voice-activity gating by default, so it is not used.
- No GPU CI tier.

See [nvidia-afx-stubs.md](nvidia-afx-stubs.md) for the readiness contract.
