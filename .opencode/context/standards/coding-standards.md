# Coding standards

Authoritative source for these rules is `AGENTS.md` at the repo root plus `docs/development/` and `docs/repository-policy.md`. This file is the working summary. If the two disagree, `AGENTS.md` wins, then source, then documentation.

## Style

| Rule | Form | Example |
|---|---|---|
| File-scoped namespaces | always, no block-scoped | `namespace Trackdub.Domain.StageRuns;` |
| `sealed` where extension is not intended | default for classes | `public sealed class WavePcm16MultiSourceMixOptInAnalyzer` |
| `Async` suffix | every async method | `Task<StageReadiness> EvaluateAsync(..., CancellationToken ct)` |
| Immutable `record` in Domain | domain value objects and entities | `public sealed record ProjectArtifact(Guid Id, ...)` |

`sealed` is the default, not the exception. Leaving a class unsealed for hypothetical derivation is scope creep. Records in Domain are immutable by design — Domain holds invariants, not mutable state.

Both directions of the async rule matter: no `Async` on a synchronous method, no missing `Async` on an asynchronous one. Flow `CancellationToken` to every await.

## Scope discipline

- Smallest change that satisfies the request. No surrounding cleanup on a bug fix.
- No abstractions for hypothetical future needs. Three similar lines beats a premature abstraction.
- No back-compat shims or feature flags unless asked.
- Prefer editing an existing file to creating a new one.
- **Never create `*.md` or README files unless explicitly asked.** Follow the taxonomy in `docs/repository-policy.md` when documentation is requested: `decisions/` ADR-NNNN, `architecture/`, `specs/`, `operations/`, `development/`, `reference/`, `legal/`, `audits/`, `plans/`, `strategy/`.
- No comments unless the *why* is non-obvious — a hidden constraint, a workaround, a subtle invariant. Do not narrate what the code does. Do not reference the current task or PR in comments.
- No WinUI. Avalonia is the shell framework; new WinUI paths are forbidden.
- No SQL in view models. No persistence in view models. Pipeline truth stays in Application/Sdk.

## Warning-as-error discipline

`Directory.Build.props` sets:

```xml
<TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<LangVersion>latest</LangVersion>
<EnableWindowsTargeting>true</EnableWindowsTargeting>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
```

Do not suppress warnings casually. A `#pragma warning disable`, a bare `NoWarn`, or a `.editorconfig` suppression is a design conversation, not a build fix. Fix the cause or escalate.

The one sanctioned exception is already configured: `WarningsNotAsErrors` carries `RS0030` (the `Path.Combine` banned-API diagnostic) because ~337 existing files still call it. That is a ratchet, not a licence.

Always validate with `-warnaserror` (see `context/standards/validation-gates.md`).

## Banned symbols

`BannedSymbols.txt` is wired repo-wide through `Microsoft.CodeAnalysis.BannedApiAnalyzers` (`PackageReference` + `AdditionalFiles` in `Directory.Build.props`). It currently lists all four `System.IO.Path.Combine` overloads — `(string,string)`, `(string,string,string)`, `(string,string,string,string)`, `(string[])` — each with the message *"Use Path.Join instead"*.

Format:

```
<method-signature>;<message>
```

**Rule: prefer `Path.Join` over `Path.Combine` in all new or changed code.** `Path.Combine` silently drops earlier segments when a later argument is rooted; CodeQL/CodeFactor flag it on every call whose argument cannot be proven non-rooted, which in practice is almost every call. `Path.Join` has no reset behavior and is a drop-in replacement throughout this codebase.

Diagnostics surface as **warnings**, not errors, today. A green build does not mean zero `Path.Combine` hits. Inspect the union of these committed-diff and worktree-diff paths for changed `Path.Combine` lines:

```bash
git diff --name-only "$BASE_REF"...HEAD -- '*.cs'
git diff --name-only HEAD -- '*.cs'
```

To add a ban, append a line to `BannedSymbols.txt` in the same format. Verify the signature is correct first — an unmatched signature silently bans nothing.

## Cross-platform portability

Cross-platform is a requirement, not a preference. Portable .NET 10 APIs by default (`net10.0`). Extended operations: `docs/operations/cloud-operations.md`.

- Windows-specific APIs need an explicit Windows leg or a guarded seam with a real fallback. `EnableWindowsTargeting` lets the Windows legs *build* everywhere; it does not make them *run* everywhere.
- CI builds and tests on Windows, Linux, and macOS (`ci.yml` jobs `build-windows`, `build-linux`, `build-macos`). A Windows-only change that breaks a Linux or macOS leg is a regression.
- Path handling: `Path.Join`, and always normalize separators when comparing across platforms (`FilePathComparison.cs` in Contracts exists for this).
- Shell out only via portable invocations; native binaries are acquired through scripts, never tracked in the repo.
- **No end-user runtime dependency** may be introduced: Python, Conda, Docker, CUDA Toolkit all count. Developer-only tooling under `tools/` is fine; it just must not become a runtime requirement.

## Testing conventions

| Layer | Rule |
|---|---|
| Domain tests | fast, pure, zero I/O |
| Application tests | fakes from `tests/Trackdub.TestDoubles/` (shared source via `<Compile Include>`) — no real models, no network |
| Pipeline tests | must cover **success**, **disabled/skipped**, **missing-prerequisite**, and **failure** |
| Scoped run | `dotnet test tests/Trackdub.<Area>.Tests --no-restore -m:1` |
| Filter | `dotnet test tests/Trackdub.Application.Tests --filter "FullyQualifiedName~<TestName>" -m:1` |

Coverage is not a gate; honest state is. A test asserting a resume must assert `EXISTING_ARTIFACTS_VALID`, not "succeeded".

## Custom analyzer

`src/Trackdub.Analyzers/WavePcm16MultiSourceMixOptInAnalyzer.cs` → `TRACKDUB001`, severity `Warning`, enabled by default. A method whose name contains `Mix`, `Mixer`, `Blend`, or `Render` that calls `Trackdub.Media.Waveforms.WavePcm16.WriteSamplesAsync` without a literal `normalizePeak: true` is flagged. Only the literal-`true` argument counts as opt-in; missing, `false`, or non-constant arguments all leave the call to hard-clip. Rationale: ADR-0012 (`docs/decisions/ADR-0012-wave-pcm16-loudness-policy.md`).

`Trackdub.Media` references the analyzer project with `OutputItemType="Analyzer"` and `ReferenceOutputAssembly="false"`, keeping the analyzer DLL out of the runtime reference surface.

## Commits

Imperative titles: `Add ...`, `Fix ...`, `Remove ...`. Always `git commit -m` — the interactive editor is broken in this environment. Stage by explicit path, never `git add -A`.

## Verify

```bash
dotnet format Trackdub.slnx --verify-no-changes
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
```

## Related

- `context/standards/validation-gates.md`
- `context/standards/architecture-rules.md`
- `context/domain/architecture.md`
- `docs/development/development.md`, `docs/repository-policy.md` — authoritative versions