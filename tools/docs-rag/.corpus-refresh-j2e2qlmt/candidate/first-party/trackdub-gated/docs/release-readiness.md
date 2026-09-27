# Release readiness checklist

What actually blocks shipping `Trackdub-gated` to a real customer, as of
2026-07-31. Each item names an owner and why it isn't closed — items with no
known value are recorded as open questions, not filled with placeholders that
look decided. Cross-reference `docs/plans/phase-2-gated-rebuild-plan.md` for
how the repo got here.

## Blocks a release today (enforced by tooling — will fail the build, not just this doc)

- **Activation issuer/audience/endpoint.**
  [`trackdub.trust.json`](../src/Trackdub.App.Avalonia/Licensing/production/trackdub.trust.json)'s
  `Issuer`/`Audience`/`ActivationEndpoint` are now provisioned values
  (`https://activate.trackdub.com`, `trackdub-license-prod`,
  `https://activate.trackdub.com/activate`) — no `REPLACE_BEFORE_PRODUCTION_RELEASE:...`
  placeholders remain, so the `verify-release` placeholder scan passes. **Owner:
  activation-backend phase** (deliberately excluded from this repo — see
  `docs/plans/trackdub-gated-split-manifest.md`). Still open: confirming a live
  activation backend actually serves that endpoint before shipping. Enforced by
  `tools/Trackdub.Licensing.Tooling verify-release`, which
  `tools/release/package-release.ps1` runs before packaging anything — see
  `docs/licensing/trust-ring-schema.md`'s "verify-release" section.

## Fixed upstream (included in current pin)
| Item | Owner | Why it isn't closed |
- **ffmpeg auto-download was broken on Windows** —
  [trackdubllc/Trackdub#23](https://github.com/trackdubllc/Trackdub/issues/23),
  closed. `FfmpegAutoDownloader`'s hardcoded Windows x64/arm64 URLs 404'd
  (BtbN pruned the dated release they were pinned to). Fixed in core
  `60f9f75`/`57c5626`/`5070e69`/`db99f56` (PR #29).
- **Software encoder fallback assumed `libx264`** —
  [trackdubllc/Trackdub#24](https://github.com/trackdubllc/Trackdub/issues/24),
  closed in core. The pinned core still deliberately selects `libx264` as its
  software encoder, so its runtime FFmpeg artifact must provide that encoder.

`external/Trackdub` is pinned to `desktop-pin-2026-07-31`
(`8ff42de206e1bcbff1d8b6a589334a9219eb858c`), which includes the Windows URL
fix; it succeeds `desktop-pin-2026-07-30` (`70f5258`), which predates it. The
submodule gitlink stores the commit SHA directly — `.gitmodules`'
`branch = main` is only a convenience default for `git submodule update
--remote` and doesn't determine the pin, so don't edit it when bumping.

## Needs your input, not invented here

- **GPL written-offer/source contact** — required legal review remains open;
  see `THIRD-PARTY-NOTICES.md`'s ffmpeg section. The pinned core's end-user
  runtime downloader targets GPL FFmpeg artifacts. The binary remains
  unbundled (and `package-release.ps1` refuses to package one), but that does
  not make the GPL-vs-LGPL question disappear. Confirm source, notice, and
  written-offer obligations for the runtime acquisition model before release.
- **Consumer EULA.** `package-release.ps1` picks up `EULA.md` from the repo
  root if present but doesn't fail without one (currently just warns) — no
  EULA text exists yet. Drafting it needs your input and real legal review;
  prior handoffs gated this work behind an approved EULA that was never
  granted.
- **Code signing (Authenticode).** Not attempted this round — needs a
  certificate decision from you before it's worth building.
- **H.264/HEVC patent licensing.** Independent of the GPL/LGPL copyright
  question — MPEG-LA/Via LA patent pools may require a separate license
  depending on jurisdiction and distribution volume/model. Flagged in
  `THIRD-PARTY-NOTICES.md`; needs real legal review, not a source inventory.
- **`AvaloniaUI.DiagnosticsSupport`'s actual license.** Its `.nuspec` has no
  license metadata at all (verified directly). Currently worked around by
  scoping the package to Debug-only builds
  (`src/Trackdub.App.Avalonia/Trackdub.App.Avalonia.csproj`), so no shipped
  artifact carries it — but the underlying "what license is this actually
  under" question is still open if a maintainer ever wants it back in
  Release for real diagnostics tooling. Would need an answer from AvaloniaUI
  OÜ directly.
- **ffmpeg GPL-vs-LGPL per-RID decision.** Now live: core's Windows
  auto-download works again and targets GPL artifacts that provide the
  hardcoded `libx264` software encoder. Confirm deliberately that GPL builds
  and their source/notice obligations are acceptable for every supported RID;
  do not describe the shipped runtime path as LGPL-only.

## Verification before treating this list as exhaustive

Re-run `docs/licensing/regenerate.ps1` (needs `nuget-license` 4.0.15 and a
generous timeout — the full-solution scan exceeds 3 minutes) and spot-check
any remaining blank-name rows in
`docs/licensing/third-party-nuget-licenses.md` before assuming the NuGet
dependency graph has no other gaps like `AvaloniaUI.DiagnosticsSupport`.
