# AGENTS.md

Guidance for agents working on Trackdub Desktop (`trackdubllc/Trackdub-gated`).

## Core Principles
1. Read `AGENT_CONTEXT.md` first (operating contract). Extended docs: `docs/user-preferences.md` and `docs/workspace-operations.md`.
2. Linear (workspace `trackdubllc`, team **TS**): search/update issues autonomously (`repo:gated`). Never mark Done without proof.
3. Conflict order: source code/tests > task instructions > `AGENT_CONTEXT.md` > `AGENTS.md` > Linear > other docs.
4. **Never fake readiness:** Provider registered != model downloaded != stage ran != stage succeeded.
5. Cross-platform is required. Target .NET 10 (`net10.0` + `net10.0-windows10.0.19041.0`).

## Architecture & Boundaries
- Only `Trackdub.App.Avalonia` and its tests live here as shipped code.
- `external/Trackdub` is a pinned read-only git submodule. **Never edit files inside `external/Trackdub`**.
- Bump submodule: `cd external/Trackdub && git fetch origin && git checkout <tag-or-sha> && cd ../.. && git add external/Trackdub && git commit -m "Bump core pin"`.
- Dependency direction:
  - `App.Avalonia` -> `external/Trackdub` (`Application`, `Composition`, `Domain`, `Licensing`, `Media.Playback`, `Sdk`)
  - `App.Avalonia.Tests` -> `external/Trackdub` (`Application`, `Composition`, `Contracts`, `Domain`, `Licensing` + App.Avalonia links)
  - `UI.Tests` -> `App.Avalonia`
- No inference code in `Trackdub.App.Avalonia`. Keep CLI in `Trackdub.Cli`, automation in `Trackdub.Sdk`. No persistence in view models.

## Commands
```powershell
# Build & Test
dotnet build Trackdub.slnx -m:1
dotnet test Trackdub.slnx -m:1

# Single test target
dotnet test tests/Trackdub.App.Avalonia.Tests --no-restore -m:1
dotnet test tests/Trackdub.UI.Tests --no-restore -m:1
dotnet test tests/Trackdub.App.Avalonia.Tests --filter "FullyQualifiedName~<TestName>"

# CI validation (warnings as errors)
dotnet build Trackdub.slnx --configuration Release -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1

# Run Desktop App
.\run.cmd                  # Preferred on Windows
dotnet run --project src/Trackdub.App.Avalonia -f net10.0-windows10.0.19041.0 --no-restore # Windows fallback
dotnet run --project src/Trackdub.App.Avalonia -f net10.0                                  # Linux/macOS
```

## packages.lock.json conflicts
Don't hand-resolve merge conflicts in `packages.lock.json`. Take either side (`git checkout --ours` or `--theirs`), then regenerate:
```powershell
dotnet restore Trackdub.slnx --force-evaluate -m:1
```

## Coding & ViewModel Patterns
- Style: File-scoped namespaces, `sealed` where extension not intended, `Async` suffix on async methods, immutable `record` in Domain.
- Treat warnings as errors (`TreatWarningsAsErrors=true`). Do not suppress casually.
- Prefer `Path.Join` over `Path.Combine` for new/changed code. `Path.Combine` silently drops
  earlier segments if a later one is rooted; CodeQL/CodeFactor flag it whenever that can't be
  proven false, which in practice is almost every call. `Path.Join` has no such reset behavior
  and is a drop-in replacement. Enforced repo-wide as a build warning via
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` (see `BannedSymbols.txt`, `Directory.Build.props`)
  — not an error yet since existing files still use `Path.Combine`. Mirrors the same setup in
  the `Trackdub` repo.
- CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`): UI state, toggles, form fields, one-off commands.
- ReactiveUI (`ReactiveObject`, `ObservableAsPropertyHelper<T>`, `.WhenAnyValue`, `.ToProperty`): live pipeline streams, playback status, reactive engine events.
- Do not mix CommunityToolkit and ReactiveUI patterns on the same property.

## Testing & UI Verification
- Repo owns `Trackdub.App.Avalonia.Tests` and `Trackdub.UI.Tests`. Fakes linked from `external/Trackdub/tests/Trackdub.TestDoubles/`.
- After `.axaml` or shell UI edits, run headless screenshot tests on Windows TFM:
  ```powershell
  $env:CAPTURE_UI_SCREENSHOTS="1"
  dotnet test tests/Trackdub.UI.Tests -f net10.0-windows10.0.19041.0 --filter "FullyQualifiedName~ComponentScreenshot"
  ```
  Inspect generated PNGs under `.design/app-shell-redesign/screenshots/headless/components/`. Never claim UI polish without fresh PNG evidence.

## Documentation Grounding
- For Trackdub implementation facts, architecture, provider wiring, and repo-specific operational guidance, use `trackdub-docs-rag` MCP tools (`search_trackdub_docs`, `ask_trackdub_docs`, `get_trackdub_doc`). Fall back to Context7 for third-party libraries. Treat retrieved docs as evidence, verify against live code.

## Model Governance
- Commercial license only for shipping lane. Enforced by bundled manifest (`bundled-models.manifest.json`).
- Unknown license = unsafe. No runtime downloads in unit tests.
- Do not add runtime dependencies on Python, Conda, Docker, or CUDA Toolkit for end users.
