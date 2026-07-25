# macOS Deployment Notes

Guidelines and constraints for shipping Trackdub on macOS.

## Build & Publish

```bash
# Self-contained publish for macOS (required for users without .NET installed)
dotnet publish src/Trackdub.App.Avalonia -r osx-arm64 -c Release --self-contained
dotnet publish src/Trackdub.App.Avalonia -r osx-x64 -c Release --self-contained
```

## Critical Constraints

### IncludeNativeLibrariesForSelfExtract

**Never set `IncludeNativeLibrariesForSelfExtract = true` for macOS targets.**

This is incompatible with macOS app bundles. Native libraries (libmpv, ONNX Runtime, espeak-ng, etc.) must be loose files inside the bundle structure, not packed into a single-file executable. macOS code signing and Gatekeeper require individual files to be inspectable.

As of July 2026, this property is not set anywhere in the Trackdub solution (verified in Directory.Build.props and all .csproj files).

### Single UI Thread

macOS allows only one UI thread. Code that spawns splash screens or secondary dispatchers on background threads will crash. Avalonia's threading model enforces this natively, and Trackdub's current code (verified July 2026) uses only `DispatcherTimer` on the main thread with no secondary UI thread creation.

### Key Mapping

Avalonia natively maps `KeyModifiers.Control` to Cmd on macOS for standard gestures (Cmd+Z, Cmd+C, etc.). Trackdub's `MainWindowShortcutRouter` checks `KeyModifiers.Control` which Avalonia translates correctly on macOS. No XPF shim layer needed (Trackdub uses native Avalonia, not XPF).

Verify custom keybindings work on macOS when adding new shortcuts.

## Code Signing (Pre-Launch)

### When to Sign

- Not required for GitHub Releases (early adopters can bypass Gatekeeper)
- Required before marketing to non-technical users (Product Hunt, ads, etc.)
- Required for Mac App Store distribution

### How to Sign

1. Apple Developer Program ($99/year) required for Developer ID certificate
2. Sign individual native libraries first, then the bundle. Do NOT use `codesign --deep` (unreliable for complex bundles)
3. Native dylibs requiring individual signing:
   - `libmpv.2.dylib`
   - `libonnxruntime.dylib` (+ provider dylibs)
   - espeak-ng libraries
   - LibVLC libraries
   - FFmpeg libraries
4. Use Avalonia Parcel for cross-platform signing (P12 cert) and notarization
5. Notarization required for macOS 10.15+ (Catalina and later)

### Entitlements

If CoreML or GPU access requires JIT, add appropriate entitlements to `Entitlements.plist`:
```xml
<key>com.apple.security.cs.allow-jit</key>
<true/>
<key>com.apple.security.cs.allow-unsigned-executable-memory</key>
<true/>
```

Verify which entitlements ONNX Runtime's CoreML EP requires before release.

## CI Screenshots

macOS headless UI screenshots are captured in CI on `macos-14` (Apple Silicon):
- Set `CAPTURE_UI_SCREENSHOTS=1` environment variable
- Tests run with `--filter "FullyQualifiedName~Screenshot|FullyQualifiedName~Layout"`
- Screenshots uploaded as `macos-ui-screenshots` artifact on each CI run
- Same headless Skia renderer as Windows (UseHeadlessDrawing = false)

## App Bundle Structure

macOS requires `.app` bundle for distribution. Use Avalonia Parcel or manual structure:

```
Trackdub.app/
  Contents/
    MacOS/
      Trackdub              (main executable)
      libmpv.2.dylib
      libonnxruntime.dylib
      ...
    Resources/
      AppIcon.icns
    Info.plist
```

Bundle identifier: `ai.trackdub.app` (or similar reverse-DNS)
