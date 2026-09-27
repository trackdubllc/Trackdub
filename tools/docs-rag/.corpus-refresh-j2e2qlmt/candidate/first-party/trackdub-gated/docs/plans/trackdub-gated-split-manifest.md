# Trackdub-gated Source-Split Manifest

Phase 2.2 of `docs/plans/open-core-split-continuation.md` (public core repo).

This document assigns a destination to every `src/`, `tests/`, and `services/`
path currently in this repository, ahead of rebuilding it as the private desktop
product repo that consumes the public core (`trackdubllc/Trackdub`) as a pinned
git submodule instead of embedded source copies.

Nothing is moved or deleted by this document. It is the reviewable plan that the
later rebuild executes.

## Baselines compared

| Side | Ref |
| --- | --- |
| This repo (gated) | `b0ef90d8` — tip of `main`, 10-commit private-import history |
| Public core | `a416ab82` — tip of `main`, includes the merged Phase 1 release-readiness work |

Every judgement below comes from a full recursive directory diff between those
two trees, not from project names.

## Headline finding: the shared projects here are stale, not divergent

Fifteen `src/` projects share a name with a public-core project. The diff shows
these are overwhelmingly **behind** the public core rather than carrying
desktop-specific work — the differences are the Phase 1 improvements that landed
upstream and never came back here:

- `Trackdub.Cli.csproj` / `Trackdub.OnnxRuntime.Dnnl.Native.csproj` — core has the
  package metadata (`Authors`, `Description`, `RepositoryUrl`, `PackageProjectUrl`,
  `PackageLicenseExpression`) added in Phase 1; this repo does not.
- `Trackdub.Media.csproj` — core wires the `Trackdub.Analyzers` project as an
  analyzer reference; this repo has no `Trackdub.Analyzers` project at all.
- `Trackdub.Contracts` / `Trackdub.Domain` / `Trackdub.Application` — core has the
  `Pipeline` transient-fault types (`PipelineTransientFault`,
  `PipelineTransientFaultRunSnapshot`, `TransientFaultSummary`,
  `PipelineTransientFaultBus`, `Trackdub.Domain/Pipeline`); this repo does not.
- `Trackdub.Application.csproj` — core has the `Trackdub.Composition.Tests`
  `InternalsVisibleTo`; this repo does not.

So for these projects the submodule is a strict upgrade. They are dropped here
and consumed from the core, and **no merge-back is required** — with the specific
exceptions itemised in the next section.

## Genuinely gated-only work (the real migration surface)

Five clusters need a destination decided rather than simply dropped. The first
four exist here and nowhere in the public core; the fifth (§5) is a
reference-retargeting consequence of keeping the desktop app. These are the only
things that need a destination decided rather than simply dropped.

### 1. Licensing trust ring + revocation — BLOCKER, needs a decision

Gated-only files:

- `src/Trackdub.Licensing/TrustRingConfiguration.cs` (169 lines)
- `src/Trackdub.Licensing/RevocationConfiguration.cs` (18 lines)
- `tests/Trackdub.Licensing.Tests/TrustRingRotationTests.cs`
- `tests/Trackdub.Licensing.Tests/RevocationBootstrapTests.cs`

But the trust-ring work is **not** additive. It rewrites core-owned types in
place:

- `LicenseTokenValidator.cs` — core verifies ES256 against a single embedded
  development public-key PEM. This repo replaces that entirely with multi-key
  trust-ring lookup plus revocation, and changes the `VerifySignature` signature
  to take a `keyId`.
- `LicenseService.cs` — gains a trust-ring constructor overload, production
  configuration validation that throws on invalid production rings, and
  rejection of dev-unlimited tokens under a production ring.
- `LicenseTokenClaims.cs` — record gains `KeyId`, `Issuer`, `Audience`.
- `LicenseTokenParser.cs` — parses the new claims.

**RESOLVED (Phase 2.3, PR #4, merged 2026-07-29): option 2 below was chosen.**
Core's `Trackdub.Licensing` gained a public `ILicenseSignatureTrustStore` seam
(answers "which key is trusted", encodes no policy). The desktop repo supplies
the concrete trust ring via
[`DesktopLicenseSignatureTrustStore.cs`](../../src/Trackdub.App.Avalonia/Licensing/DesktopLicenseSignatureTrustStore.cs)
(wraps `TrustRingConfiguration`/`RevocationConfiguration`, both gated-only) plus
[`ProductionLicensePolicyInitializer.cs`](../../src/Trackdub.App.Avalonia/Licensing/ProductionLicensePolicyInitializer.cs),
which decorates `ILicenseInitializer`/`ILicenseTierProvider` to reject
dev-unlimited tokens under a production trust ring — the one piece of policy
the core seam intentionally doesn't encode. Revocation-signature verification
was fixed post-merge (commit `0167b14`); the production trust ring is now
provisioned from JSON with revocation required for production keys (commit
`c65b652`). Kept for the record, the original three options considered follow:

Under a submodule model the desktop repo consumes core sources read-only; it
cannot patch `Trackdub.Licensing` in place. Three options, none free:

1. **Upstream the trust ring into the public core.** Simplest structurally, and
   the validator changes are arguably generic hardening rather than proprietary.
   Cost: production key-rotation and revocation policy becomes publicly visible
   (the mechanism, not the keys). Needs an explicit call on whether that is
   acceptable, since it is the enforcement path for paid licensing.
2. **Add extension points to core's `Trackdub.Licensing`** — core exposes a
   signature-verification/trust-provider seam, desktop supplies the concrete
   trust ring. Keeps policy private, but means designing and landing a public API
   in core first, and this becomes a core change before it is a desktop change.
3. **Move licensing wholesale into the desktop repo** and drop it from core.
   Cleanest privacy story, but core's own `Trackdub.Licensing.Tests` and the
   `LicensingIsolationTests` architecture test depend on it existing there.

Recommend option 2 — it is the only one that keeps the revocation/rotation policy
private without deleting a project the public core still tests against. It does
mean a public-core PR lands before this repo is rebuilt.

### 2. Two `IExportTierGate` implementations — NOT redundant

The plan assumed a single implementation. There are two, semantically identical
(same 5-minute free-tier limit, same watermark rule, same lazy-init pattern):

| File | Registered by |
| --- | --- |
| `src/Trackdub.App.Avalonia/Licensing/DesktopExportTierGate.cs` | `DesktopLicensingComposition.AddDesktopLicensing()`, called from `App.axaml.cs:134` |
| `src/Trackdub.Application/Licensing/ExportTierGate.cs` | `Trackdub.Composition/CompositionRoot.cs:501` (`TryAddSingleton`) |

The `Trackdub.Application` copy is gated-only — the public core ships no
concrete implementation, which is the intended open-core boundary and is stated
in core's own `IExportTierGate` doc comment. (This repo's copy of that interface
still carries the stale comment "Implemented in Application layer", which Phase 1
corrected upstream.)

Because `CompositionRoot` uses `TryAddSingleton` and `AddDesktopLicensing()` uses
`AddSingleton`, registration order currently decides which one wins at runtime —
fragile, and it only works at all because both live in the same repo.

**Do not simply drop the `Trackdub.Application` copy — that removes tier
enforcement from every headless export path.** The two registrations are not
redundant; they serve different hosts:

- `AddDesktopLicensing()` is called only from `App.axaml.cs`, so it covers the
  Avalonia app alone.
- Headless hosts (`Trackdub.Cli`, `Trackdub.Sdk`) enter through
  `HeadlessCompositionRoot.AddHeadlessTrackdub`, which calls `AddTrackdub()`,
  whose inline "Licensing services" block is what supplies their gate.

`ExportStageHandler` takes the gate as optional (`IExportTierGate? = null`) and
fails **open** — `if (exportTierGate is not null)` guards the duration check and
`exportTierGate?.RequiresWatermark == true` guards the watermark. A null gate
therefore means no duration limit and no watermark, silently.

The public core registers no `IExportTierGate` at all (confirmed: its
`CompositionRoot` has no such line, and its `ExportStageHandler` is identically
fail-open). That is the intended open-core boundary. But it means that once the
shared projects come from the submodule, dropping the gated registration leaves
headless exports in the rebuilt desktop repo ungated.

**RESOLVED (Phase 2.3, PR #4, merged 2026-07-29): first bullet applied.** The
desktop repo ships no headless CLI/SDK of its own — it consumes core's
Apache-2.0 `Trackdub.Cli` unmodified, which is intentionally fail-open/ungated.
`Trackdub.Application/Licensing/ExportTierGate.cs` and its `CompositionRoot`
registration were dropped entirely rather than ported (matches task #16's
resolution). `DesktopExportTierGate.cs` remains, registered only through
`AddDesktopLicensing()` for the Avalonia app — no dual-registration ambiguity
remains since the core copy no longer exists.

Kept for the record — a historical snapshot of the considerations *before* the
resolution above, not current guidance. Do not reintroduce the dropped
registration based on this section:

Destination: keep `DesktopExportTierGate`. The `Trackdub.Application` copy and
its `CompositionRoot` registration were, at the time this was written, **held
pending a decision** (now resolved: dropped, not ported — see above):

- If the desktop product never ships or exposes a headless/CLI export path, the
  gap is theoretical and both can be dropped — core's CLI and SDK are
  Apache-2.0 with no tier gating by design.
- If it does, the desktop repo needs its own headless registration, or core
  needs the extension seam described in §1.

Option 2 in §1 resolves both problems with one seam, which strengthens the case
for it.

### 3. Cloud quarantine architecture test

`tests/Trackdub.Architecture.Tests/CloudQuarantineTests.cs` is gated-only; core's
`Trackdub.Architecture.Tests` instead has `DependencyGraphTests.cs`,
`LicensingIsolationTests.cs`, and `StageNameConsistencyTests.cs`. Since the cloud
lane is being excluded entirely (below), this test's subject disappears with it.
Destination: drop, unless the cloud lane is preserved elsewhere first.

### 4. Desktop UI test suites — do NOT move as-is

`tests/Trackdub.UI.Tests` has no core counterpart and is desktop-only
(screenshot, layout, fullscreen chrome, platform service tests).
`tests/Trackdub.App.Avalonia.Tests` likewise. Both belong in the desktop repo,
but neither can be copied across unchanged.

**These two projects do not build in this repository today.** Both link test
doubles by relative source path, and `tests/Trackdub.TestDoubles` does not exist
here — it lives only in the public core. The seven links are therefore already
dangling at `b0ef90d8`:

| Project | Lines | Linked files |
| --- | --- | --- |
| `Trackdub.App.Avalonia.Tests.csproj` | 128–131 | `FakeConsentService`, `FakeLocalAssistant`, `FakeModelInventoryService`, `FakeStudioSettingsService` |
| `Trackdub.UI.Tests.csproj` | 30–32 | `FakeOperationRunner`, `FakeGlossaryRepository`, `FakeGlobalGlossaryRepository` |

All seven exist in core's `tests/Trackdub.TestDoubles` (72 fakes total), so the
submodule supplies them — but only once the paths are retargeted. Note the two
projects use different relative prefixes (`..\..\tests\...` vs `..\...`), so a
single find-and-replace will not cover both.

Two further complications for Phase 2.3 scaffolding:

- `Trackdub.App.Avalonia.Tests` carries **86** `Compile Include` links in total.
  The bulk point at `..\..\src\Trackdub.App.Avalonia\...` and stay valid since
  that project is kept, but every path is repo-root-relative and breaks the
  moment the directory depth changes.
- It also holds five `ProjectReference`s — `Trackdub.Application`,
  `Trackdub.Composition`, `Trackdub.Contracts`, `Trackdub.Domain`,
  `Trackdub.Licensing` — all of which become submodule paths. `Trackdub.UI.Tests`
  references only `Trackdub.App.Avalonia` and is the simpler of the two.

Destination: **keep**, with mandatory path retargeting. Phase 2.3 must decide
between retargeting each link into `external/Trackdub/tests/Trackdub.TestDoubles`
or introducing a shared `.props` import that centralises the test-double source
set for both projects. The second is preferable — it gives one place to fix when
the submodule pin moves.

### 5. Desktop production project references — also need retargeting

The same problem applies to the shipping app, not just its tests.
`src/Trackdub.App.Avalonia/Trackdub.App.Avalonia.csproj:107-115` carries six
`ProjectReference`s, and **every one** resolves to a sibling `src/` directory
that becomes a submodule path:

| Reference | Note |
| --- | --- |
| `Trackdub.Application` | |
| `Trackdub.Sdk` | |
| `Trackdub.Composition` | carries `SetTargetFramework` metadata |
| `Trackdub.Domain` | |
| `Trackdub.Licensing` | also subject to the §1 trust-ring decision |
| `Trackdub.Media.Playback` | carries `SetTargetFramework` metadata |

Two of these pass `SetTargetFramework` metadata, so a naive path rewrite that
drops the child elements will break the multi-targeted build. The kept desktop
project does not compile until all six are retargeted into `external/Trackdub`.

## Full path assignment

Destinations: **keep** (moves into the rebuilt desktop repo) · **core** (dropped
here, consumed from the submodule) · **cloud-exclude** · **activation-exclude**.

### `src/` — 19 projects

| Path | Destination | Note |
| --- | --- | --- |
| `Trackdub.Api` | cloud-exclude | per `migration/cloud-legacy-quarantine.json` |
| `Trackdub.App.Avalonia` | **keep** | the desktop product; sole desktop-unique project |
| `Trackdub.Application` | core | gated-only `Licensing/ExportTierGate.cs` **resolved: dropped** (§2), not ported |
| `Trackdub.Benchmarks` | core | gated copy stale |
| `Trackdub.Cli` | core | gated copy missing Phase 1 package metadata |
| `Trackdub.Composition` | core | gated-only `IExportTierGate` registration **resolved: dropped** (§2), not ported |
| `Trackdub.Contracts` | core | gated copy missing `Pipeline` types; stale doc comment |
| `Trackdub.Domain` | core | gated copy missing `Pipeline/` |
| `Trackdub.Inference` | core | |
| `Trackdub.Inference.Onnx` | core | |
| `Trackdub.Infrastructure` | core | |
| `Trackdub.Licensing` | core + desktop | core gained `ILicenseSignatureTrustStore` seam; desktop supplies trust ring/revocation/policy (§1, **resolved**) |
| `Trackdub.Media` | core | gated copy missing analyzer reference |
| `Trackdub.Media.Playback` | core | |
| `Trackdub.OnnxRuntime.Dnnl.Native` | core | gated copy missing Phase 1 package metadata |
| `Trackdub.Sdk` | core | |
| `Trackdub.Tools` | core | |
| `Trackdub.WebhookDelivery` | cloud-exclude | AWS/Lambda-dependent |
| `Trackdub.Worker` | cloud-exclude | AWS/Lambda-dependent |

### `tests/` — 8 projects

| Path | Destination | Note |
| --- | --- | --- |
| `Trackdub.Api.Billing.Tests` | cloud-exclude | |
| `Trackdub.Api.Tests` | cloud-exclude | carries AWSSDK package refs |
| `Trackdub.App.Avalonia.Tests` | **keep** | needs path retargeting; does not build today (§4) |
| `Trackdub.Architecture.Tests` | core | except gated-only `CloudQuarantineTests.cs` → drop (§3) |
| `Trackdub.Cloud.Tests` | cloud-exclude | |
| `Trackdub.Licensing.Tests` | core + desktop | trust-ring tests now live in `Trackdub.App.Avalonia.Tests` (§1, **resolved**) |
| `Trackdub.UI.Tests` | **keep** | needs path retargeting; does not build today (§4) |
| `Trackdub.Worker.Tests` | cloud-exclude | |

### `services/` — 1

| Path | Destination | Note |
| --- | --- | --- |
| `activation-service` | activation-exclude | future `api.trackdub` phase, per plan §2.2 |

### Repository-root build/scaffolding files

Not individually assigned — Phase 2.3 scaffolding rewrites these anyway:
`Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
`NuGet.Config`, `global.json`, `Trackdub.Cloud.sln`, `Trackdub.Cloud.slnf`,
`AGENTS.md`, `AGENT_CONTEXT.md`, `README.md`. The two `Trackdub.Cloud.sln*` files
and `migration/` are cloud/staging artifacts and are expected to be dropped.

**`AGENTS.md` is not purely cosmetic and needs its own Phase 2.3 acceptance
criterion.** Core's `tests/Trackdub.Architecture.Tests/DependencyGraphTests.cs`
— which the rebuilt repo inherits from the submodule, since gated's
`Trackdub.Architecture.Tests` destination is core per §3 — asserts that
`AGENTS.md`'s dependency diagram matches every `src/**/*.csproj`
`ProjectReference` exactly, in both directions (extra project, missing project,
or extra/missing edge all fail the test). `FindRepoRoot()` resolves relative to
wherever the test runs, so this check applies to the rebuilt repo's own
`AGENTS.md` and its own (much smaller, `Trackdub.App.Avalonia`-only) `src/`
tree — not to core's. Regenerating `AGENTS.md` to describe the new, mostly
submodule-backed layout and running this test is required before Phase 2.3 can
be considered done, not an optional cleanup step.

### Repository-root product inputs — these are NOT scaffolding

`assets/`, `native/`, `resources/`, and `runtime/` are consumed directly by the
kept desktop app and must be assigned explicitly. `Trackdub.App.Avalonia.csproj`
reads from all four by repo-root-relative path, including two unconditional
references (`ApplicationIcon` at line 12 and the `app.ico` `Content` at line 41),
so leaving them unassigned breaks the rebuilt app.

| Path | Destination | Basis |
| --- | --- | --- |
| `assets/` | **keep** | Disjoint from core's `assets/`, which holds only `demo-media`. Gated holds `icons/` — the app icon, consumed unconditionally. |
| `native/` | **keep** | Does not exist in core at all. Holds `win-x64/libmpv-2.dll`, the desktop playback binary. |
| `runtime/` | **split** | `win-native-deps.manifest.json` is gated-only → keep. `trt-rtx-ep.manifest.json` is identical to core's → core. |
| `resources/` | **core** | Byte-identical to core's `resources/` (olive-recipes). Consume from the submodule. |

Two consequences for Phase 2.3, both belonging to the same retargeting work as
§4 and §5:

- The `resources/olive-recipes/**` globs in `Trackdub.App.Avalonia.csproj` must be
  retargeted to `external/Trackdub/resources/...`.
- The `native/` libmpv binaries are `Exists()`-guarded per RID, so a broken path
  fails **silently** — the app builds and ships without its playback library
  rather than erroring. Worth a build-time assertion when this is wired up.

`native/` also carries a redistributed LGPL binary, which is an input to the
Phase 2.5 third-party notice work rather than something this manifest settles.

## Coverage check

19 `src` + 8 `tests` + 1 `services` = 28 paths listed, each exactly once, matching
the 28 directories present at `b0ef90d8`. No path is unassigned or double-counted.

The four root product-input directories (`assets/`, `native/`, `resources/`,
`runtime/`) are assigned separately above. Remaining root entries are build
scaffolding that Phase 2.3 regenerates.

## Blocking items before the rebuild can proceed

1. **Licensing trust-ring decision (§1)** — architectural, blocks the rebuild, and
   likely requires a public-core PR first.
2. **Headless export-tier enforcement (§2)** — decide whether the desktop product
   ships or exposes a headless/CLI export path. If it does, dropping the gated
   `CompositionRoot` registration silently removes free-tier duration limits and
   watermarking for those hosts, because `ExportStageHandler` fails open on a
   null gate. Resolved together with §1 if the seam option is taken.
3. **Desktop reference retargeting (§4, §5)** — mechanical, but it must be planned
   into Phase 2.3 rather than discovered during the rebuild. Both kept test
   projects and the shipping `Trackdub.App.Avalonia` reference paths that only
   exist inside the submodule; two of the six production references carry
   `SetTargetFramework` metadata that a naive rewrite would drop.
4. **Immutable core pin** — Phase 2.3 consumes an annotated core tag. None has
   been cut; `a416ab82` is the candidate.
5. **`AGENTS.md` regeneration** — core's `DependencyGraphTests` inherits into the
   rebuilt repo via the submodule and fails the build if `AGENTS.md`'s
   dependency diagram doesn't exactly match the rebuilt repo's own
   `src/**/*.csproj` graph. Regenerate and validate it as part of Phase 2.3, not
   after.
6. **History preservation** — this session cannot create repositories or push
   tags to `main` (both 403; pushes are scoped to the working branch). Owner
   states the original monorepo is already archived separately, which covers
   this. Worth re-confirming immediately before the history rewrite, since that
   step is irreversible against this repo's URL.
