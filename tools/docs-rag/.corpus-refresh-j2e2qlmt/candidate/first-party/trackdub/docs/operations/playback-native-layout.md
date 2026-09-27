# Playback native runtime layout (libmpv / LibVLC)

Avalonia video preview uses composited frame backends. Native libraries must be present where
[`LibMpvRuntimeLocator`](../../src/Trackdub.Media.Playback/LibMpvRuntimeLocator.cs) and
[`LibVlcRuntimeLocator`](../../src/Trackdub.Media.Playback/LibVlcRuntimeLocator.cs) probe,
relative to `AppContext.BaseDirectory` (the published app folder).

**Primary strategy:** ship libmpv under `native/{rid}/` in publish output. LibVLC on Windows and
macOS comes from NuGet (`VideoLAN.LibVLC.Windows` / `VideoLAN.LibVLC.Mac`). On Linux, LibVLC is
typically the system package (`vlc` / `libvlc`).

**Fallback (optional):** startup bootstrap may download libmpv into the user profile when bundled
copies are missing (Windows `%LocalAppData%\Trackdub\native\{rid}`, macOS
`~/Library/Application Support/Trackdub/native/{rid}`). Do **not** rely on bootstrap for portable
releases or CI artifacts.

**Resolution order:** `LibMpvRuntimeLocator` checks `native/{rid}/` next to the app (walking up from
`AppContext.BaseDirectory`) **before** user-profile bootstrap paths. A stale download under
`%LocalAppData%` must not override a good bundled DLL beside the executable.

## Fetch scripts (dev / release)

| OS | Command | Output |
|----|---------|--------|
| Windows x64 | `.\tools\dev\Fetch-WinNativeDeps.ps1 -Architecture X64` | `native/win-x64/libmpv-2.dll` |
| Windows ARM64 | `.\tools\dev\Fetch-WinNativeDeps.ps1 -Architecture Arm64` | `native/win-arm64/libmpv-2.dll` |
| macOS | `pwsh ./tools/dev/Fetch-MacNativeDeps.ps1` (on Mac) | `native/osx-x64/libmpv.2.dylib` or `native/osx-arm64/...` |

Manifest: [`runtime/win-native-deps.manifest.json`](../../runtime/win-native-deps.manifest.json).

`tools/dev/Build-TrackdubAvalonia.ps1` and release CI fetch Windows (and macOS release jobs fetch
mac) when artifacts are missing.

## Windows x64 (published app folder)

```text
{AppContext.BaseDirectory}/
  Trackdub.App.Avalonia.exe
  native/
    win-x64/
      libmpv-2.dll          # also accepts libmpv-1.dll, mpv-2.dll, mpv-1.dll
  libvlc/                   # from VideoLAN.LibVLC.Windows
    win-x64/
      libvlc.dll
      libvlccore.dll
      plugins/...
```

Flat `{base}/libmpv-2.dll` is also probed when walking parent directories.

## macOS

```text
{AppContext.BaseDirectory}/
  native/
    osx-arm64/              # or osx-x64
      libmpv.2.dylib        # also libmpv.1.dylib, libmpv.dylib
  libvlc/                   # from VideoLAN.LibVLC.Mac
    osx-arm64/
      libvlc.dylib
      libvlccore.dylib
      plugins/...
```

User-cache fallback: `~/Library/Application Support/Trackdub/native/{rid}/`.

## Linux

**libmpv (optional bundle):**

```text
native/
  linux-x64/
    libmpv.so.2             # also libmpv.so.1, libmpv.so
```

**LibVLC (default: system install):**

- Bundled: `libvlc/linux-x64/libvlc.so` (if you ship a tree).
- Otherwise locator checks `/usr/lib`, `/usr/lib/x86_64-linux-gnu`, `/usr/lib/aarch64-linux-gnu`,
  `/usr/lib64` for `libvlc.so` / `libvlc.so.*`.

Install example: `sudo apt install vlc` (Debian/Ubuntu).

## Backend selection

[`AvaloniaPlaybackCapabilityProbe`](../../src/Trackdub.App.Avalonia/Playback/AvaloniaPlaybackCapabilityProbe.cs)
sets `PreferredBackend = LibMpv` when `ILibMpvRuntimeLocator.ResolveRuntimeLibraryPath()` is
non-null; otherwise `LibVlc`.

[`PlaybackService`](../../src/Trackdub.Media.Playback/PlaybackAbstractions.cs) retries LibVLC if
LibMpv open fails.

## Verification checklist

1. **Locators** — After publish or `dotnet run`, paths resolve:
   - `ILibMpvRuntimeLocator.ResolveRuntimeLibraryPath()` → non-null when `native/{rid}/` is present.
   - `ILibVlcRuntimeLocator.ResolveRuntimePath()` → non-null (bundled `libvlc/` or Linux system path).

2. **Probe** — Open a project with video; UI `PlaybackSummary` should show `Backend: LibMpv` when
   libmpv is bundled.

3. **Preview** — `IsPlaybackBackendAvailable` true; compositor delivers a frame (`HasVideoFrame` true,
   playback placeholder hidden).

On failures, check `%LOCALAPPDATA%\Trackdub\trackdub.log` (Windows) or platform log path.

## Debug logging

In **Debug** / `TRACKDUB_DEV_BUILD`, [`AvaloniaPlaybackComposition`](../../src/Trackdub.App.Avalonia/Playback/AvaloniaPlaybackComposition.cs)
logs resolved libmpv and LibVLC paths once at startup (when a logger is available).
