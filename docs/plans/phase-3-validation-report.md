# Phase 3 validation report

Status: complete for what tooling in this session could verify. Ad hoc audit
run 2026-07-31, not a recurring CI gate — see "Gaps" below for what still
needs a human or different tooling.

Cross-reference `open-core-split-continuation.md`'s Phase 3 section, which
this report fulfills.

## 1. Dependency direction

**Verified: `Trackdub` has no reference to `Trackdub-gated`.**

```
grep -rEln "Trackdub-gated|App\.Avalonia|DesktopExportTierGate|DesktopLicensingComposition" \
  --include="*.cs" --include="*.md" --include="*.props" --include="*.targets" --include="*.slnx" \
  src/ docs/ *.props *.targets *.slnx
```

Two source-code hits, both deliberate seams, not violations:

- `src/Trackdub.Media.Playback/PlaybackAbstractions.cs:257` — comment
  explaining why a member is `public` rather than `internal` (the presenter
  that constructs it lives in `Trackdub.App.Avalonia`, a different assembly
  in a different repo).
- `src/Trackdub.Application/Properties/AssemblyInfo.cs:4` —
  `[assembly: InternalsVisibleTo("Trackdub.App.Avalonia")]`, the actual
  friend-assembly grant that seam requires.

`Directory.Packages.props` has one comment mentioning `App.Avalonia.Tests`
(a pending-migration note about xunit v2 vs FsCheck 3.x) — informational,
not a dependency.

Every other hit is in `docs/` (plans, specs, audits, architecture, decisions,
operations, development, strategy) — historical/internal-engineering
documents that legitimately predate or describe the split. Per
`docs/repository-policy.md`'s conflict order, historical plans are evidence,
not binding implementation truth, and are explicitly out of scope for the
project's own boundary scanner (see below).

**Confirmed direction: `Trackdub-gated → Trackdub` (via pinned submodule),
never the reverse in buildable code.**

## 2. Boundary scan

**Ran the repo's existing scanner rather than writing a new one:**

```
$ python3 scripts/ci/check-repository-boundary.py
Repository-boundary scan passed.
```

This checks for stale monorepo-era license claims (GPL/dual-license wording
outside legitimate third-party-notice discussion) and desktop/cloud project
names bleeding into files a public consumer would read as authoritative. It
already special-cases `src/Trackdub.Media/Process/FfmpegAutoDownloader.cs`
(legitimately discusses GPL ffmpeg builds) and excludes `docs/plans/`,
`docs/decisions/`, `docs/audits/`, `docs/architecture/`, `docs/specs/`,
`docs/operations/`, `docs/development/`, and `tools/` as historical/internal.

No stale `Trackdub-Monorepo-Archive`, `BSL`, or `Business Source License`
references found anywhere in `docs/legal/`, `src/`, or root `.props` files.

## 3. Package metadata

**Verified: both publishable packages carry correct, distinct license
metadata.**

Only two projects declare `PackageId`/`IsPackable` in this repo:

| Project | License | Why |
|---|---|---|
| `Trackdub.Cli` | `Apache-2.0` | Trackdub-owned, matches root `LICENSE` |
| `Trackdub.OnnxRuntime.Dnnl.Native` | `MIT` | Third-party-derived native package; kept distinct per Phase 1.4's requirement to preserve real upstream metadata rather than blanket-applying Apache-2.0 |

`Directory.Build.props` sets no repo-wide package metadata (no
`PackageLicenseExpression`, `RepositoryUrl`, etc.) — correct, since it
would incorrectly apply to `Trackdub.OnnxRuntime.Dnnl.Native`'s MIT license
if it did. Each packable project sets its own metadata explicitly instead.

No other project in `src/` is packable, so no other metadata gap exists.

## 4. Branch protection

**Not verified — no tool available.** The GitHub MCP server this session had
access to exposes no branch-protection or repository-ruleset read/write
tools (checked via broad keyword search across its full tool surface).
Recording this as unverified rather than guessing at either repo's actual
settings.

Action needed from you: check `Settings → Branches` (or `Settings → Rules →
Rulesets`) directly on `trackdubllc/Trackdub`, and decide/record whatever you
find. `Trackdub-gated` is private — per your note, branch protection rules
aren't available for it on GitHub's private-repo tier; the compensating
controls already in place are the PR-merge convention in gated's `AGENTS.md`,
the `REVIEW.md` reviewer checklist, and green-CI-before-merge, all recorded
as an accepted gap, not unfinished work.

## Gaps / follow-ups

- **Branch protection state on `Trackdub`** — needs a human check, see above.
- **Stale GitHub labels on `Trackdub`** — `repo:quickshell`,
  `repo:numan-plugins`, `repo:numan-registry`, `repo:olive-studio` still
  exist on the label set (confirmed via `get_label`) from when this repo's
  issue tracker briefly served as a catch-all for unrelated projects. No
  label-delete tool was available this session; delete manually via
  `Settings → Labels`.
- **Closed cross-project issues #8–#14** — flagged in the original audit for
  your skim (closed but world-readable on a now-public repo); not re-checked
  in this pass.
