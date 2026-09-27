## LibVLCSharp.Avalonia Playback Plan

### Summary
Add a LibVLC-backed playback path for the Avalonia shell so cross-platform video playback works on Windows, macOS, and Linux with app-bundled `libvlc` runtimes. Keep the first slice narrow: real play/pause/seek/duration/current-position, honest runtime-unavailable errors, and optional VLC-managed subtitle tracks or external sidecar subtitles. Do not attempt arbitrary Avalonia overlays on top of the video in this slice.

### Key Changes
- Add a new playback backend kind for the shared seam:
  - Extend `PlaybackBackendKind` with `LibVlc`.
  - Keep existing `MediaFoundation`, `FfmpegFallback`, and `LibMpvFallback` values for compatibility; do not repurpose them.
  - Update backend labels/warnings so `LibVlc` reports as the selected backend and missing-runtime failures are explicit.

- Keep WinUI playback untouched; make Avalonia choose LibVLC through shell-specific composition:
  - In the WinUI app, continue using the existing `PlaybackCapabilityProbe` + `DefaultPlaybackBackendFactory`.
  - In `Trackdub.App.Avalonia`, override the playback registrations with:
    - an Avalonia-specific capability probe that prefers `LibVlc` for local video playback,
    - a LibVLC backend factory,
    - a LibVLC host control instead of WinUI `MediaPlayerElement`.
  - This avoids forcing WinUI’s Media Foundation assumptions onto the Avalonia shell.

- Add a new LibVLC playback implementation in `Trackdub.Media.Playback`:
  - `LibVlcPlaybackBackend : IPlaybackBackend, IPlaybackHostAwareBackend, IPlaybackRateBackend, IPlaybackVolumeBackend, IDisposable`
  - Host type should be Avalonia/LibVLC-specific and owned by the Avalonia app, but the backend stays in `Media.Playback`.
  - `TryAttachHost` should bind the backend to the Avalonia `VideoView`.
  - `OpenAsync` should:
    - validate the source path,
    - initialize `LibVLC`/`MediaPlayer`,
    - load local file media,
    - surface missing runtime or open failure as a warning in `PlaybackSnapshot`,
    - not fabricate `IsLoaded` when VLC failed to prepare media.
  - `GetSnapshotAsync` should report duration, current position, play/pause state, rate, and warning/error text.

- Add bundled VLC runtime resolution for the Avalonia app:
  - Introduce a small runtime locator/service that resolves the bundled `libvlc` root relative to the Avalonia app output.
  - App-bundled runtime is the only supported first-slice mode.
  - Missing or malformed runtime bundle must produce a structured unavailable/error state, not silent fallback.
  - Keep this entirely separate from inference/model readiness.

- Add Avalonia player hosting and shell wiring:
  - Replace the current placeholder playback area with a LibVLC Avalonia `VideoView` host.
  - Keep the current view model-driven commands for play, pause, seek, and playback status.
  - Do not layer sibling Avalonia controls over the native video host.
  - If simple in-video UI is needed, place it inside the LibVLC `VideoView` container only.

- Subtitle scope for this slice:
  - Support only VLC-managed subtitles:
    - embedded subtitle tracks when present,
    - optionally an external sidecar subtitle file generated from existing project subtitle data.
  - Do not port the current WinUI arbitrary CC overlay behavior yet.
  - If no usable subtitle track/sidecar is available, playback still works and the UI reports subtitles as unavailable.

- Subtitle integration shape:
  - Reuse existing project subtitle data/export logic where possible instead of inventing a parallel Avalonia subtitle model.
  - Generate a temporary SRT sidecar from the current transcript or translated cues only when the user explicitly enables subtitles in the Avalonia shell.
  - Feed that sidecar to VLC for rendering; keep the subtitle toggle honest about what source is active.
  - Clean up temporary sidecars with the same artifact/persistence discipline already used elsewhere; do not overwrite source media.

- Package and dependency changes:
  - Add central package pins in `Directory.Packages.props` for the LibVLCSharp Avalonia packages and any runtime package(s) needed for bundled desktop distribution.
  - Keep package additions scoped to the Avalonia app and playback layer.
  - Record packaging/license notes for VLC redistribution in repo docs where third-party runtime packaging is already described.

### Interfaces / Public Shape
- Shared playback seam:
  - Add `PlaybackBackendKind.LibVlc`.
  - Update `PlaybackControlViewModel` backend label/warning mapping for the new kind.
- Avalonia composition:
  - Add shell-specific playback registrations in `Trackdub.App.Avalonia` rather than changing the Windows composition root behavior globally.
- No new Avalonia-only project/session model.
- No changes to inference/provider readiness contracts.

### Test Plan
- Playback abstraction tests:
  - `PlaybackService` opens through `LibVlc` when the Avalonia probe selects it.
  - Missing VLC runtime returns `IsBackendAvailable = false` or loaded=false with explicit warning text, depending on final backend contract choice.
  - Play, pause, seek, rate, and volume flow through the LibVLC backend.
  - Open failure surfaces warning text without pretending playback loaded.

- Avalonia shell tests or focused integration checks:
  - Build `src/Trackdub.App.Avalonia`.
  - Verify project open/create still works with the LibVLC host present.
  - Verify playback state updates in the view model when media is loaded and when runtime is unavailable.

- Subtitle scenarios:
  - Embedded subtitle track present: toggle enables VLC subtitle display.
  - External generated sidecar available: VLC loads it and reports subtitles active.
  - No subtitles available: toggle is disabled or reports unavailable without affecting playback.

- Manual smoke matrix:
  - Windows Avalonia shell with bundled runtime: MP4/H.264 plays.
  - macOS Avalonia shell with bundled runtime: MP4/H.264 plays.
  - Linux Avalonia shell with bundled runtime: MP4/H.264 plays.
  - Missing bundled runtime on any platform: explicit unavailable/error state.
  - Non-MP4 format that VLC can handle: plays if runtime is valid, without changing project/media persistence behavior.

### Assumptions
- First-slice supported playback target is “reliably play local video,” not “match WinUI overlay composition.”
- App-bundled VLC runtime is acceptable for Windows/macOS/Linux packaging and licensing review.
- In-video subtitles may be VLC-rendered, but arbitrary Avalonia overlays over the video are out of scope for this slice.
- H.264/MP4 remains the minimum-confidence smoke target even if VLC can play more formats.


