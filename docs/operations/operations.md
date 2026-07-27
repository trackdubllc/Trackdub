# CodeQL advanced setup (Trackdub)

Trackdub uses **advanced CodeQL only** via [`.github/workflows/codeql.yml`](../../.github/workflows/codeql.yml). Do not run GitHub **default** CodeQL in parallel on this repository.

## Why one setup

| | Default CodeQL (`CodeQL` workflow) | Advanced (`CodeQL Advanced`) |
|---|-----------------------------------|------------------------------|
| Source | Org/repo dynamic setup | `.github/workflows/codeql.yml` |
| C# build | `build-mode: none` on Linux | Manual `Trackdub.sln` build on Windows |
| Frontend (JS/TS) | Default | `build-mode: none` (JS/TS does not support manual builds) |
| Queries | Default suite | `security-extended,security-and-quality` |
| Paths | Whole repo | `.github/codeql/codeql-config.yml` scopes |

Running both wastes Actions minutes and produces weaker C# results (no compiled DB).

## Current state (check periodically)

```powershell
# Repo default-setup flag (want: state = not-configured)
gh api repos/trackdubllc/Trackdub/code-scanning/default-setup --jq '{state, query_suite}'

# Applied org security configuration (default setup should be disabled when advanced-only)
gh api repos/trackdubllc/Trackdub/code-security-configuration --jq '.configuration | {name, code_scanning_default_setup, code_scanning_options}'

# Recent Advanced runs
gh run list --repo trackdubllc/Trackdub --workflow=codeql.yml --limit 5
```

If you see **both** `CodeQL` and `CodeQL Advanced` on the same push, default setup is still enabled at org or repo level.

## One-time org fix (requires org admin)

The `trackdubllc` org applies enforced configuration **`trackdubllc-org-config-1`**, which currently enables **Code scanning default setup**. That spawns the dynamic `CodeQL` workflow even when the repo workflow file is advanced.

**UI (recommended):**

1. Open [trackdubllc org security configuration](https://github.com/organizations/trackdubllc/settings/security_products/configurations/edit/259214).
2. Under **Code scanning**, set **Default setup** to **Disabled**.
3. Keep **Allow advanced setup** enabled.
4. Save.

**API (needs `admin:org` on `gh auth`):**

```powershell
gh auth refresh -h github.com -s admin:org

@'
{
  "code_scanning_default_setup": "disabled",
  "code_scanning_options": {
    "allow_advanced": true
  }
}
'@ | gh api -X PATCH orgs/trackdubllc/code-security/configurations/259214 --input -
```

**Repo-level backup:**

1. Repository **Settings → Advanced Security → Code security**.
2. **CodeQL analysis** menu → **Switch to advanced** (or disable default CodeQL if offered).
3. Confirm `.github/workflows/codeql.yml` remains the active workflow.

Then confirm repo default setup:

```powershell
gh api repos/trackdubllc/Trackdub/code-scanning/default-setup -X PATCH -f state=not-configured
```

## Verify Advanced

```powershell
gh workflow run codeql.yml --repo trackdubllc/Trackdub
gh run list --repo trackdubllc/Trackdub --workflow=codeql.yml --limit 1
```

Expect four matrix jobs: `actions`, `csharp` (Windows), `javascript-typescript`, `python`.

## Related workflows

- **`code-coverage.yml`**: Coverlet + GitHub Code Quality upload (test coverage, not SAST).
- **`ci.yml`**: Build/test gate; does not replace CodeQL.

## Config files

- Workflow: `.github/workflows/codeql.yml`
- Query/path config: `.github/codeql/codeql-config.yml`

# GitHub Actions Workflows

CI/CD lives in `.github/workflows/`. Windows jobs use self-hosted runners; Linux jobs use `self-hosted`.

**Workflows do not auto-run on push or pull request.** Start them manually or from PR comments:

| Command (PR comment) | Workflow |
|----------------------|----------|
| `/ci` | Full CI (format + Windows/Linux build/test) |
| `/oc` or `/opencode` | OpenCode bot |

Manual dispatch still works:

```powershell
gh workflow run ci.yml
gh workflow run release.yml -f tag=v1.2.3
gh workflow run cursor-code-review.yml -f pull_request_number=123
gh workflow run opencode.yml -f prompt="Summarize recent pipeline changes"
```

## Active workflows

### CI (`ci.yml`)

- **Trigger:** PR comment `/ci`, or manual (`workflow_dispatch`)
- **Jobs:**
  - **Verify Code Format** (self-hosted): `dotnet format Trackdub.sln --verify-no-changes`
  - **Build & Test (Windows):** restore/build/test `Trackdub.sln` (Release, `-m:1`)
  - **Build & Test (Linux):** restore/build/test `Trackdub.Avalonia.slnf` on `net10.0`; tests run per project via `scripts/ci/run-avslnf-tests-sequential.sh`
- **Timeout:** 45 minutes per build matrix leg

### Dependabot Auto-Merge (`dependabot-auto-merge.yml`)

- **Trigger:** Automatically runs on `pull_request` when Dependabot opens or updates a PR
- **Jobs:**
  - **Auto-merge Dependabot PR:** fetches Dependabot metadata, approves the PR, and enables auto-merge with `--squash` via `gh` CLI. It ensures that once all required status checks/tests pass on the PR, the PR is automatically and safely merged.

### Release (`release.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `tag` input e.g. `v1.2.3`)
- **Jobs:** Solution tests, Windows release build, Linux/macOS-style Unix publish matrix, GitHub Release upload

### API deploy (`api-deploy.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted
- **Tasks:** Docker build, ECR push, ECS task render + deploy

### Model manifest audit (`model-audit.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted
- **Tasks:** `tools/ci/audit-bundled-model-manifest.py`

### Cursor code review (`cursor-code-review.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `pull_request_number`)
- **Runs:** `ubuntu-latest`
- **Tasks:** Deno 2 runs `tools/cursor-sdk-agent` via `@cursor/sdk`; posts/updates a single PR comment
- **Secret:** `CURSOR_API_KEY` (repository secret)

### OpenCode review (`opencode-review.yml`)

- **Trigger:** Manual (`workflow_dispatch`, required `pull_request_number`)
- **Runs:** self-hosted
- **Tasks:** `anomalyco/opencode/github` reviews the PR via OpenRouter (comment-only prompt; must not commit/push)
- **Secret:** `OPENROUTER_API_KEY` (repository secret); uses `GITHUB_TOKEN` for GitHub API

### OpenCode on demand (`opencode.yml`)

- **Trigger:** PR comment `/oc` or `/opencode`, or manual (`workflow_dispatch` with `prompt`)
- **Runs:** ubuntu-latest
- **Tasks:** Runs OpenCode with the supplied prompt

### TRT RTX smoke (`trt-rtx-smoke.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Runs:** self-hosted Windows when `TRACKDUB_TRT_RTX_SMOKE == 'true'`

### Frontend build (`frontend-build.yml`)

- **Trigger:** Manual (`workflow_dispatch`)
- **Tasks:** `pnpm install --frozen-lockfile` + Vite production build for `frontend/`

### CodeQL Advanced (`codeql.yml`)

- **Trigger:** Push/PR to `main`, weekly schedule (Mon 01:42 UTC), manual (`workflow_dispatch`)
- **Runs:** `ubuntu-latest` (actions, JS/TS, Python — `build-mode: none`); `windows-latest` (C# manual `Trackdub.sln` build)
- **Tasks:** Advanced CodeQL with `security-extended,security-and-quality`; path config in `.github/codeql/codeql-config.yml`
- **Important:** Only canonical CodeQL workflow for this repo. Disable GitHub default CodeQL (org `trackdubllc-org-config-1` or repo settings) to avoid duplicate dynamic `CodeQL` runs. See `docs/internal/codeql-advanced-setup.md`.

```powershell
gh workflow run codeql.yml
gh run list --workflow=codeql.yml --limit 3
```

### Code coverage (`code-coverage.yml`)

- **Trigger:** Push/PR to `main`, manual (`workflow_dispatch`)
- **Runs:** `ubuntu-latest`
- **Tasks:** Coverlet on `Trackdub.Avalonia.slnf`, ReportGenerator merge, `actions/upload-code-coverage`, PR comment

## Secrets (deploy + review)

| Secret | Purpose |
|--------|---------|
| `AWS_DEPLOY_ROLE_ARN` | OIDC role for API deploy |
| `ECS_EXECUTION_ROLE_ARN` / `ECS_TASK_ROLE_ARN` | ECS task definition |
| `AWS_ACCOUNT_ID` / `EFS_FILE_SYSTEM_ID` | Task definition substitution |
| `CURSOR_API_KEY` | Cursor SDK PR review (`cursor-code-review.yml`) |
| `OPENROUTER_API_KEY` | OpenCode PR review (`opencode-review.yml`, `opencode.yml`) |

## Local parity

```powershell
dotnet format Trackdub.sln --verify-no-changes
dotnet build Trackdub.sln -c Release -m:1
dotnet test Trackdub.sln -c Release --no-build -m:1
dotnet build Trackdub.Avalonia.slnf -c Release -f net10.0 -m:1
./scripts/ci/run-avslnf-tests-sequential.sh Trackdub.Avalonia.slnf "--framework net10.0"
deno task validate
```

Last updated: 2026-07-11

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
