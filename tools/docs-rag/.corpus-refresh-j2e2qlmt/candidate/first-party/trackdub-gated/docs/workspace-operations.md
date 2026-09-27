# Operational & Environment Reference

Detailed operational facts and environment-specific instructions for Trackdub Desktop (`Trackdub-gated`).

## Architecture & Submodule Structure
- `external/Trackdub`: Pinned read-only submodule containing public core (`Application`, `Composition`, `Contracts`, `Domain`, `Inference`, `Inference.Onnx`, `Infrastructure`, `Licensing`, `Media`, `Media.Playback`, `OnnxRuntime.Dnnl.Native`, `Sdk`, `Tools`). Never edit directly inside this directory in this repo.
- `Trackdub.Contracts` -> `Trackdub.Domain` coupling is intentional per `external/Trackdub/docs/decisions/ADR-0011-contracts-domain-coupling.md`.

## Runtime, Playback, and Media Pipeline
- **Target Frameworks:** Multi-targets `net10.0` (Linux/macOS) and `net10.0-windows10.0.19041.0` (Windows). Keep both TFMs healthy.
- **Playback Engine:** **libmpv** is primary; **LibVLC** fallback when libmpv fails. FFmpeg bootstraps separately. See `external/Trackdub/docs/operations/playback-native-layout.md`.
- **Media Ingest:** Fast media import (`CreateMediaSpineAsync`) registers source only. Pipeline stages require `ArtifactKind.NormalizedAudio` via `EnsureNormalizedAudioAsync` (FFmpeg extraction) before separation/cleanup/ASR.
- **Session & Preferences:**
  - Session data stored in SQLite under project folder.
  - Bundled ONNX inventory resides in manifest/cache JSON, not SQLite.
  - Project mix and export UI preferences persist in `ProjectUiSettings` via manifest (`mix`/`export` keys in `ProjectDocuments`), not SQLite.
- **Model Downloader:** Parallel ranged downloader (`ParallelRangeDownloader`, `TRACKDUB_HF_*` env) for HF downloads. Must handle bulk missing files, stale partials, and graceful cancellation.
- **Execution Providers (Windows):**
  - **TensorRT RTX:** Standalone ONNX Runtime EP ABI plugin (`NvTensorRTRTXExecutionProvider`) registered from DLL bundle; never bootstrap downloads automatically.
  - **Windows ML:** Integration surface for DirectML and catalog EPs (MIGraphX/OpenVINO/QNN/VitisAI).
  - **DirectML:** Legacy GPU fallback.
  - Docs: `external/Trackdub/docs/decisions/ADR-0002-windows-ml-provider-strategy.md` and `external/Trackdub/docs/reference/tensorrt-rtx-ep-abi-plugin.md`.

## CI & Code Quality Gates
- **GitHub Actions:** CI runs on push to `main` and pull requests to `main` across Windows, Ubuntu, and macOS runners.
- **CodeQL:** Canonical scanner is `.github/workflows/codeql.yml` (`CodeQL Advanced`) only. Do not enable GitHub default CodeQL in parallel.
- **GitLab CI:** `.gitlab-ci.yml` exists as mirror/legacy; not canonical gate.
- **Attribution:** If bundled ONNX model has `requires_attribution: true`, update `THIRD_PARTY_NOTICES.md`.

## Cursor Cloud / Linux Environment Notes
- Linux VM (no Windows). .NET SDK 10.0.300 under `~/.dotnet` on `PATH`.
- Always build/run on Linux with `-f net10.0` (`net10.0-windows10.0.19041.0` is Windows-only).
- Headless GUI runs on `DISPLAY=:1` (Xvfb). First launch displays starter dialog; dismiss ("Skip for now"). App log: `~/.local/share/Trackdub/trackdub.log`.
- PreToolUse/PostToolUse hooks in `.claude/settings.json` call Node scripts in `.claude/hooks/`.
