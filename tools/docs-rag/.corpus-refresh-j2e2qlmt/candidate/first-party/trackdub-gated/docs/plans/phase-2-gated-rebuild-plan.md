# Trackdub Open-Core Split — Phase 2: Rebuild Trackdub-gated

## Context

Phase 1 (public-core release readiness on `trackdubllc/Trackdub`) is complete and merged (PR #6). The owner has since decided to deprioritize the two remaining Phase 1 blockers (GHAS/CodeQL, self-hosted runner availability) since the repo is going public soon regardless, and asked to move on to **Phase 2** of `docs/plans/open-core-split-continuation.md`: rebuild `trackdubllc/Trackdub-gated` as the final private desktop-product repository, with fresh history, consuming the public core via a pinned git submodule instead of embedded source copies.

This phase is materially riskier than Phase 1: it involves discarding/replacing an existing repository's git history (an irreversible action against that repo's canonical URL unless a full mirror is preserved first) and migrating real product code across repos. I explored `trackdubllc/Trackdub-gated`'s actual current state rather than trusting the plan doc's assumptions, and I'm scoping this round of work to the safe, reviewable groundwork — not the destructive history-replacement step — per the platform's guidance to confirm before hard-to-reverse actions and per the plan doc's own sequencing (build the manifest, *then* copy/replace).

## Verified Current State

- **`trackdubllc/Trackdub-gated`**: private, single branch `main`, HEAD `b0ef90d8` ("Strengthen cloud quarantine validation"), **10 commits total**, all authored by the same person within a 72-minute window on 2026-07-14 — a shallow "private import" history exactly as the plan doc describes, not something to carry forward as the final desktop repo's history. No branch protection configured. No `.gitmodules` / no existing submodule reference to `Trackdub`.
- **Root `README.md`** explicitly documents three lanes: **desktop** (`src/Trackdub.App.Avalonia`, `src/Trackdub.Licensing`, their tests), **activation** (`services/activation-service`), and **cloud-legacy quarantine** (`src/Trackdub.Api`, `src/Trackdub.Worker`, `src/Trackdub.WebhookDelivery`, cloud tests) — plus a machine-readable `migration/cloud-legacy-quarantine.json` formalizing the same split. States outright: "This repository is source staging only. It is not release-ready."
- **`src/` (19 projects)**: only **`Trackdub.App.Avalonia`** is unambiguously desktop-unique. Three (`Trackdub.Api`, `Trackdub.Worker`, `Trackdub.WebhookDelivery`) are cloud-exclude per the repo's own quarantine manifest. The remaining 15 share a project name with the already-published public core (`Trackdub.Application`, `Trackdub.Contracts`, `Trackdub.Domain`, `Trackdub.Licensing`, etc.) — **not verified identical**, need a real diff before assuming they're superseded by the submodule rather than divergent.
- **`services/activation-service/`** exists (TypeScript/Cloudflare Workers). The plan doc's Phase 2.2 exclusion list explicitly excludes this from the final gated history (activation server work is a separate future `api.trackdub` phase) — confirmed present, will be excluded, not migrated.
- **No `frontend/`, `docker/`, or `infra/`** directory exists in this repo (unlike the monorepo-era docs elsewhere reference) — nothing to exclude there.
- **The concrete `IExportTierGate` implementation** (the whole point of Phase 2.4) is already cleanly isolated: `src/Trackdub.App.Avalonia/Licensing/DesktopExportTierGate.cs` (implements `Trackdub.Contracts.IExportTierGate`, owns the 5-minute free-tier duration limit and watermark decision) plus `DesktopLicensingComposition.cs` (the `AddDesktopLicensing()` DI registration). Both live exclusively under `Trackdub.App.Avalonia/Licensing/` — no other implementation exists elsewhere in the repo. This is a clean, minimal migration target, not something requiring a search across multiple candidate implementations.
- **Public core (`trackdubllc/Trackdub`) current head**: `a416ab82` on `main` (includes the merged Phase 1 PR) — this is the candidate SHA to pin the desktop repo's submodule to, once a final immutable core pin is cut per the Phase 1 plan's "Publish-readiness verification" step (not yet formally cut — worth flagging, not blocking this round's groundwork).

## STATUS: Phase 2.4 merged (2026-07-31) — orphan-root history rebuild formally dropped

**Phase 2.4** (bootable Release, immutable core pin, third-party notices) shipped via **PR #5** (merge commit `321465e`, merged 2026-07-31), CI green on both self-hosted Windows and Linux legs. Core submodule was subsequently advanced to commit `8ff42de206e1bcbff1d8b6a589334a9219eb858c`, tagged `desktop-pin-2026-07-31` on `trackdubllc/Trackdub` (successor to the historical `desktop-pin-2026-07-30` / `70f5258` pin) — see `docs/release-readiness.md` for the current pin reference.

**The Step 12 orphan-root history rebuild (raised as still-open in the Phase 2.3 status note directly below) is formally dropped, decided explicitly with the user 2026-07-31.** `main` keeps its merged history back through the original 10-commit staging import; the force-with-lease cutover described in Steps 9–15 below will not be executed. The Step 10 approval-gate artifact (old main SHA, validated tree SHA, literal force-with-lease command, rollback branch) is void — do not treat it as pending work in any future session. If a fresh single-root history is ever wanted again, that requires a new explicit decision, not a resumption of this one.

A follow-on round (branch `phase-2.5/release-packaging-and-licensing`) added the release packaging pipeline (`tools/release/package-release.ps1`, `.github/workflows/release.yml`, `verify-release` verb) that `docs/licensing/trust-ring-schema.md` had flagged as a real gap after Phase 2.4, plus licensing/legal corrections — see `docs/release-readiness.md` for the current release-blocker checklist.

## STATUS: Phase 2.3 merged (2026-07-29) — history preserved, not orphan-rebuilt

`phase-2/gated-rebuild-validation` was merged into `main` via normal PR merge (**PR #4**, merge commit `495f7f6`, merged 2026-07-29T05:19:53Z), with CI green on both Windows and Linux (run `30424759434`, ~6 min). `main` is now 56 commits deep and retains full history back through the original 10-commit staging import — **the destructive force-push orphan-history cutover described in Step 12 below never happened, and was not explicitly abandoned in any recorded decision; it was superseded by a normal merge instead.** ~~If a truly fresh, single-root history for `Trackdub-gated` is still wanted, that is unfinished work, not completed work — flag with the user before assuming it's off the table.~~ **Resolved 2026-07-31, see the Phase 2.4 status section above: formally dropped.**

Everything below this point in the doc (Steps 1–15, the approval-gate artifact) describes the *validation-topology plan as originally designed*, which stopped being updated after commit `affb8e2` (Step 7, CI still turning green). The real branch kept moving for ~30 more commits after that entry and picked up scope beyond the original 15-step plan. See "Post-`affb8e2` work" below for what actually shipped. Historical steps are left in place for the record but should not be read as current status.

## Post-`affb8e2` work (commits `3e71c0b`..`1c516f9`, merged as PR #4)

- **CI infra pivot**: moved to self-hosted Windows/Linux runners (`ecde0b5`, `5237e0e`, `a74183d`), fixed submodule test failures under that runner set.
- **Package-lock / restore hardening**: multiple iterations enforcing `--locked-mode` restore, TFM-graph matching, and package-lock resets specifically for the Linux leg (`e5cceb6`, `a31edf5`, `9b8da3a`, `50cc3a2`, `f04607d`, `fc7bd6c`), ending in `1c516f9` documenting *why* Linux CI omits locked-mode restore (root-caused, not silently disabled).
- **Licensing follow-through**: `0167b14` fixed the revocation signature verification gate (a correctness bug, not just polish); `c65b652` provisioned the production trust ring from JSON and made revocation mandatory for production keys — this closes the "no production ring provisioned yet" gap the plan flagged as a follow-up at the `affb8e2` checkpoint.
- **Review process**: `abd988c` added `REVIEW.md` (PR checklist for this repo); `ae3aff7`/`18a49de`/`12e84ea` applied CodeRabbit/Cubic automated review fixes.
- **Automation**: `48949e9` requires agents to sync Linear autonomously; `d1d802d`/`fc5fac6` added and pinned a Linear Release sync step in CI; `c2578f1` added a Dependabot auto-merge GitHub Action.
- **Docs**: `a100e03` fixed a broken heading in `AGENTS.md`.

**Status as of 2026-07-31** (was "not yet verified as done" — now resolved one way or the other, see the Phase 2.4 status section above for detail):
- Orphan-root history rebuild (Step 12) — **formally dropped**, decided explicitly with the user. Not superseded-and-maybe-revisitable; dropped.
- Immutable core pin on `trackdubllc/Trackdub` — historical tag `desktop-pin-2026-07-30` (`70f5258`) was superseded; the current submodule gitlink is the bare SHA `8ff42de206e1bcbff1d8b6a589334a9219eb858c`.
- Phase 2.4 (moving `DesktopExportTierGate`/`DesktopLicensingComposition`, desktop-owned test doubles) — **done**, shipped via PR #5.
- Phase 2.5 (legal/EULA/native-notice work) — **still not started**. EULA drafting explicitly gated behind an approved consumer EULA that was never granted; tracked in `docs/release-readiness.md`.

## Scope For This Round (original — superseded, kept for history)

Phases 2.1–2.3 execution (Phase 2.1 complete, Phase 2.2 in progress, Phase 2.3 in validation-topology mode). The destructive force-push cutover (Phase 2.3 Step 12) is explicitly gated behind user approval and will be presented separately for decision, not executed in this round. Phases 2.4–2.5 deferred pending Phase 2.3 completion.

1. **Preserve the current staging repo (Phase 2.1)**: create a new private repository `trackdubllc/Trackdub-gated-Staging-Archive` and push a full mirror of `Trackdub-gated`'s current history (all 10 commits, `main` branch) into it via `mcp__github__create_repository` + a local git clone/push (same mechanism used for `Trackdub` in Phase 1). This is additive/reversible — it doesn't touch the existing `Trackdub-gated` repo at all.
   - Note: I don't have a "rename repository" tool available (same gap as the repo-visibility change from Phase 1) — so I can't literally rename `Trackdub-gated` to the archive name as the plan doc suggests. Creating a separate mirror repo achieves the same preservation goal without needing that capability.
2. **Build the source-split manifest (start of Phase 2.2)**: write a reviewable manifest document at `docs/plans/trackdub-gated-split-manifest.md`, committed on a topic branch in `Trackdub-gated` and opened as a PR against its `main` — same workflow as Phase 1's PR #6, so it's reviewable, diffable, and has a permanent record in the repo rather than living only in chat. Listing, for every `src/`/`tests/`/`services/` path in `Trackdub-gated`:
   - Destination: desktop-keep / already-covered-by-public-core (exclude, consume via submodule instead) / cloud-exclude (drop entirely) / activation-exclude (drop entirely, future `api.trackdub` concern).
   - For the 15 ambiguous shared-name projects, a real diff against the corresponding public-core project (via `mcp__github__get_file_contents` on both sides, focused on `.csproj` files and any obviously desktop-specific types) to confirm each is either (a) safely superseded by the submodule with no gated-only additions, or (b) has gated-only members that need to move somewhere (most likely into the `Trackdub.App.Avalonia/Licensing` desktop layer alongside `DesktopExportTierGate`, following the same pattern). `Trackdub.Licensing` specifically needs this treatment since `DesktopExportTierGate` depends on `ILicenseTierProvider`/`ILicenseInitializer` — need to confirm whether those already exist in public-core's `Trackdub.Licensing` or are gated-only.

**Also saved to the repo**: `docs/plans/phase-2-gated-rebuild-plan.md` on branch `phase-2/gated-rebuild-validation` of `trackdubllc/Trackdub-gated` (pushed as commit `148ae53`) — keep both copies in sync when updating status; the repo copy is the durable one that survives outside this agent session.

## Phase 2.3 Live Status (updated as work proceeds — read this first)

- **Step 1 (record state)**: DONE. Old main = `de1e5e864bbed7749fe7581c66417d2563108415`.
- **Step 2 (freeze automation)**: DONE (no active automation to freeze).
- **Step 3 (validation branch)**: DONE. `phase-2/gated-rebuild-validation` created from main, pushed.
- **Step 4 (construct proposed tree)**: DONE (pending CI confirmation). Pushed as of commit `ae6431b`, then `fc49669` (CI workflow).
  - `external/Trackdub` submodule added, pinned to core SHA `5351d89b07edcec6a37166f76aaf9ba657fd1b47` (has licensing trust-store seam).
  - cloud-exclude removed: `src/Trackdub.Api`, `src/Trackdub.Worker`, `src/Trackdub.WebhookDelivery`, `tests/Trackdub.Api*.Tests`, `tests/Trackdub.Cloud.Tests`, `tests/Trackdub.Worker.Tests`, `Trackdub.Cloud.sln*`.
  - activation-exclude removed: `services/activation-service/`, `migration/`.
  - core-consumed (manifest destination "core") removed from src/tests, now consumed only via submodule: `src/Trackdub.Application`, `Trackdub.Benchmarks`, `Trackdub.Cli`, `Trackdub.Composition`, `Trackdub.Contracts`, `Trackdub.Domain`, `Trackdub.Inference`, `Trackdub.Inference.Onnx`, `Trackdub.Infrastructure`, `Trackdub.Licensing`, `Trackdub.Media`, `Trackdub.Media.Playback`, `Trackdub.OnnxRuntime.Dnnl.Native`, `Trackdub.Sdk`, `Trackdub.Tools`; `tests/Trackdub.Architecture.Tests`, `tests/Trackdub.Licensing.Tests`.
  - `resources/` removed (byte-identical to submodule's); `runtime/trt-rtx-ep.manifest.json` removed (identical), `runtime/win-native-deps.manifest.json` kept (gated-only).
  - **Reference retargeting DONE**: 6 `ProjectReference`s in `Trackdub.App.Avalonia.csproj` + resource globs → `external/Trackdub/...`; `Trackdub.App.Avalonia.Tests.csproj`/`Trackdub.UI.Tests.csproj` ProjectReferences + test-double `Compile Include`s → `external/Trackdub/tests/Trackdub.TestDoubles/...`. The 86 `Compile Include`s of App.Avalonia's own source in `.Tests.csproj` did NOT need changes (directory depth unchanged, App.Avalonia itself didn't move).
  - **Build scaffolding DONE**: new `Trackdub.slnx` (desktop + external/Trackdub projects, no cloud/activation); `Directory.Build.targets` gained the cross-platform Windows-TFM-strip block (copied from core, needed now that Composition/Inference.Onnx build via submodule); `Directory.Build.props`/`NuGet.Config`/`global.json` needed no changes (verified via diff against core — MSBuild resolves each side's Directory.Build.* independently since the submodule carries its own, so no cross-contamination). `AGENTS.md` dependency diagram + Project section + Commands section rewritten for the submodule layout (some lower-priority sections — Testing details, Cursor Cloud specifics — still reference the old monorepo and are flagged as follow-up, not blocking).
  - **§1/§2 licensing decision — DONE, this was the big one**:
    - New `src/Trackdub.App.Avalonia/Licensing/DesktopLicenseSignatureTrustStore.cs` implements core's `ILicenseSignatureTrustStore`, wrapping `TrustRingConfiguration`+`RevocationConfiguration` (moved into the same folder, namespace changed to `Trackdub.App.Avalonia.Licensing`).
    - New `src/Trackdub.App.Avalonia/Licensing/ProductionLicensePolicyInitializer.cs` decorates `ILicenseInitializer`/`ILicenseTierProvider` to reject dev-unlimited tokens under a production trust ring (the one piece of policy the core seam intentionally doesn't encode). Takes interfaces not the sealed `LicenseService`, so it's unit-testable without real signed JWTs.
    - `DesktopLicensingComposition.AddDesktopLicensing()` signature changed: now takes `(TrustRingConfiguration trustRing, RevocationConfiguration? revocation = null)`, wires `LicenseService`'s new 3-arg (trust-store) ctor from core, and registers the decorator chain. Call site in `App.axaml.cs` updated to pass `TrustRingConfiguration.Development()` explicitly (preserves the implicit dev-key behavior the app always had — no production ring is provisioned yet, that's a real follow-up, not a regression).
    - §2 (headless export-tier enforcement): resolved by DROPPING `Trackdub.Application/Licensing/ExportTierGate.cs` entirely rather than porting it — desktop ships no headless CLI/SDK of its own in this repo (consumes core's Apache-2.0 `Trackdub.Cli` unmodified, which is intentionally fail-open/ungated). Matches task #16's prior resolution.
    - Ported trust-ring/revocation test coverage as new focused unit tests: `tests/Trackdub.App.Avalonia.Tests/DesktopLicenseSignatureTrustStoreTests.cs` (6 cases, no real JWTs needed — trust store only resolves keys) and `ProductionLicensePolicyInitializerTests.cs` (4 cases, stub `ILicenseInitializer`/`ILicenseTierProvider` pair). `DesktopLicensingCompositionTests.cs` updated for the new signature and decorator types.
  - Local clone doing this work: `/tmp/trackdub-gated-work/Trackdub-gated` (branch `phase-2/gated-rebuild-validation` checked out, submodule detached at pinned SHA `5351d89`).
- **Step 5 (desktop licensing layer)**: DONE, folded into Step 4 above (see §1/§2 licensing decision).
- **Step 6 (CI on validation branch)**: DONE — old `cloud-legacy.yml` removed (validated the now-gone `Trackdub.Cloud.slnf`), replaced with new `.github/workflows/ci.yml` (Windows + Linux build/test of `Trackdub.slnx`, submodule checkout). Pushed as commit `fc49669`.
- **Additional polish (not a numbered step, done while waiting on CI)**: `README.md` rewritten from the old three-lane staging description to match the actual submodule-consuming repo; `AGENTS.md` further cleaned of stale cloud/frontend/portal references and Cursor Cloud npm-install step; unused cloud/API `PackageVersion` entries removed from `Directory.Packages.props` (AWSSDK.*, Amazon.Lambda.*, Stripe.net, JwtBearer/IdentityModel, Swashbuckle, AspNetCore.OpenApi/OpenApi, Serilog.AspNetCore, OpenTelemetry.* — verified via grep that nothing in this repo's own csproj files reference them; central-package entries with less clear boundaries, e.g. Dapper/Lucene.Net/ONNX runtime packages, deliberately left alone without build verification). Also ran a static sanity pass confirming every `ProjectReference`, `Compile Include`, and `Content Include` path in this repo's own csproj files (excluding the submodule) resolves to a real file — only the RID-specific `native/*.dll`/`.so` gaps showed up, and those are expected (`Exists()`-guarded, only `win-x64` is checked in, matches manifest note).
- **Step 7 (run CI, confirm green)**: IN PROGRESS, one real bug found and fixed so far.
  - Runs on commits `fc49669`, `148ae53`, `11e94a8`, `2976c61`, `f8cf7ae` were all auto-cancelled by the `cancel-in-progress` concurrency group before finishing (each push superseded the last) — **not informative either way, ignore them**.
  - First run to actually finish: commit `de1ecf8`, run id `30249601623`. **Linux leg FAILED** (job `89924382363`), Windows leg was still running when Linux failed (job `89924382346`, never got a completed verdict on `de1ecf8` since the branch moved before it finished — check fresh, don't assume). Pulled the actual failure log via `mcp__github__get_job_logs` (not guessed) — two distinct bugs, both fixed in commit `affb8e2`:
    1. `NETSDK1046: The TargetFramework value ';net10.0' is not valid`. Root cause: the Windows-TFM-strip regex added to `Directory.Build.targets` in commit `694330e` only strips a *leading* `;` before `net10.0-windows...`. `Trackdub.App.Avalonia.csproj` intentionally lists the Windows TFM **first** (`net10.0-windows10.0.19041.0;net10.0`, so `dotnet run` on Windows resolves the Win-targeted graph — see the csproj's own comment). With no leading `;` to consume, stripping left a dangling `;net10.0`. Fixed by appending `.Trim(';')` to the regex-replace result — correct regardless of which TFM comes first. `Trackdub.App.Avalonia.Tests.csproj` and `Trackdub.UI.Tests.csproj` were unaffected since both already OS-condition their `TargetFrameworks` directly and never contain a raw multi-TFM string with Windows first.
    2. `CS0246: The type or namespace name 'TrustRingConfiguration' could not be found` (and 3 similar errors) in `Trackdub.App.Avalonia.Tests.csproj`. Root cause: when the new gated-only licensing files (`TrustRingConfiguration.cs`, `RevocationConfiguration.cs`, `DesktopLicenseSignatureTrustStore.cs`, `ProductionLicensePolicyInitializer.cs`) were added under `src/Trackdub.App.Avalonia/Licensing/` in commit `ae6431b`, only the two *pre-existing* `Compile Include` links in the test project (`DesktopExportTierGate.cs`, `DesktopLicensingComposition.cs`) were checked — the 4 new files were never added as source links, so the test project (which links App.Avalonia's source rather than referencing the exe project — see the csproj's existing pattern) couldn't see them. The actual `Trackdub.App.Avalonia.csproj` build itself succeeded the whole time (confirmed in the log: `Trackdub.App.Avalonia -> ...dll`) — this only broke the test project. Fixed by adding the missing 4 `Compile Include` entries.
  - Fix pushed as commit `affb8e2`. **Not yet confirmed green — this is the next thing to check on resume.** Do not assume these were the only bugs; if this run also fails, pull the actual log again rather than guessing.
- **Steps 8–15**: NOT STARTED. Blocked on Step 7 (need a green run) before tree-equivalence hashing (Step 8) and the approval-gate presentation (Step 10) make sense. Given how many pushes have happened, when Step 7 finally goes green, re-run the static path-resolution sanity checks (documented above) against the actual final commit before trusting it, and recompute the tree SHA — don't reuse an old one from earlier in this log.

## Step 10 approval-gate artifact (draft — fill in workflow conclusions once CI is confirmed green, then present to user verbatim before Step 12)

- **Old main SHA**: `de1e5e864bbed7749fe7581c66417d2563108415`
- **Validation branch**: `phase-2/gated-rebuild-validation`
- **Latest validation commit SHA**: `affb8e2` (full: check `git log origin/phase-2/gated-rebuild-validation` — this keeps moving; check before trusting)
- **Validated tree SHA**: `e865ae6812649ea9f7ecf0cf53b776765b7a0b64` (as of commit `affb8e2`) — **not yet CI-validated, this is the commit with the just-pushed fix, awaiting its own CI run**
- **Core submodule pin**: `5351d89b07edcec6a37166f76aaf9ba657fd1b47` (`external/Trackdub`, has the licensing trust-store seam)
- **CI**: STATUS PENDING on commit `affb8e2` (the fix for the two bugs found in `de1ecf8`'s run — see Step 7 above). Check `mcp__github__actions_list` method `list_workflow_runs` on `ci.yml` filtered to branch `phase-2/gated-rebuild-validation`, then pull job-level status (not just run-level) since the run-level `status` can say `in_progress` while individual legs have already failed — use `list_workflow_jobs` with the run id to see per-leg (Windows/Linux) status, and `mcp__github__get_job_logs` with `return_content: true` to read an actual failure rather than guessing from job names.
- **Proposed root commit SHA**: NOT YET MINTED — Step 9 (orphan root commit prep) hasn't run. This is deliberately deferred until the validation branch is confirmed green and stable, since minting it earlier would need re-minting on every subsequent validation-branch push.
- **Proposed root tree SHA**: same as "Validated tree SHA" above once the orphan commit is minted (the orphan commit reuses this tree; only its parent-less-ness and metadata differ from `11e94a8`).
- **Literal force-with-lease command** (do NOT run without explicit approval):
  ```
  git push --force-with-lease=main:de1e5e864bbed7749fe7581c66417d2563108415 origin <proposed-root-commit-sha>:main
  ```
- **Rollback branch**: NOT YET CREATED — `phase-2/gated-rebuild-rollback` gets created pointing at `de1e5e864bbed7749fe7581c66417d2563108415` immediately before cutover (Step 11), not now, so it can't go stale relative to main in the meantime.

**Do not advance past this point without the user explicitly reviewing this artifact and approving the force-push.**

**If resuming cold**:
1. `cd /tmp/trackdub-gated-work/Trackdub-gated && git status` — if this directory doesn't exist, re-clone `trackdubllc/Trackdub-gated`, checkout `phase-2/gated-rebuild-validation` (pushed to origin at commit `fc49669` as of this update — check `git log origin/phase-2/gated-rebuild-validation` for newer), and `git submodule update --init`.
2. Check CI status: `mcp__github__actions_list` method `list_workflow_runs`, repo `Trackdub-gated`, resource_id `ci.yml` — or just check the run URL above.
3. If CI is red: read the job logs, fix, commit, push, re-check. Common risk areas to check first: submodule checkout auth (Trackdub is public so default `GITHUB_TOKEN` should suffice, but verify), the retargeted `ProjectReference`/`Compile Include` paths (typos are the most likely failure), and whether `Trackdub.App.Avalonia`'s multi-target Windows build needs anything the TFM-strip in `Directory.Build.targets` doesn't cover on the Linux leg.
4. If CI is green: proceed to Step 8 (tree equivalence proof) and Step 10 (present approval-gate artifact) per the topology below. Do NOT proceed to Step 12 (force-push cutover) without explicit user approval — that gate is unchanged.

## Phase 2.3: Rebuild Trackdub-gated with Fresh Orphaned History (Validation-First Topology)

**Architecture**: 15-step non-destructive validation topology with explicit approval gate before force-push. Desktop repo consumes public core `Trackdub` (pinned to SHA `5351d89b07edcec6a37166f76aaf9ba657fd1b47`) via `external/Trackdub` git submodule, excludes cloud-legacy and activation-service paths entirely, retains only desktop and shared layers (with 15 ambiguous projects verified against public core via Phase 2.2 manifest).

**Steps**:

1. **Record current state**: Capture `Trackdub-gated` main SHA, branch list, CI status, automation settings (completed: main `de1e5e86`, no automation active, cloud-legacy workflow green)
2. **Freeze automation**: Document that no CI/Dependabot changes will be made during rebuild window (already minimal, human discipline sufficient)
3. **Create validation branch**: `phase-2/gated-rebuild-validation` with shared history (parent = current main), working tree replaced with proposed fresh gated structure
4. **Construct proposed tree**:
   - `external/Trackdub` submodule pinned to core SHA `5351d89b07edcec6a37166f76aaf9ba657fd1b47`
   - Desktop roots: `src/Trackdub.App.Avalonia/`, `tests/Trackdub.App.Avalonia.Tests/`, `tests/Trackdub.UI.Tests/` (gated-only desktop shell and its tests; all other runtime libraries are consumed via the `external/Trackdub` submodule per the Phase 2.2 split manifest)
   - Public-core consumer roots: `src/Trackdub.Application/`, `src/Trackdub.Contracts/`, `src/Trackdub.Domain/`, and 12 others (via Phase 2.2 manifest, verified as submodule-safe or consolidated into desktop layer)
   - Root files: `Trackdub.slnx`, `Directory.Build.props`, `Directory.Build.targets`, `NuGet.config` (regenerated to consume submodule, exclude cloud paths, register desktop layer)
   - Exclude entirely: `src/Trackdub.Api/`, `src/Trackdub.Worker/`, `src/Trackdub.WebhookDelivery/`, `services/activation-service/`, all cloud tests
5. **Desktop licensing layer**:
   - `src/Trackdub.App.Avalonia/Licensing/DesktopExportTierGate.cs` (5-min free tier + watermark) + `DesktopLicensingComposition.cs` remain in place
   - Confirm `ILicenseTierProvider`, `ILicenseInitializer`, `ILicenseSignatureTrustStore` all exist in submodule's public `Trackdub.Licensing/` (via Phase 2.2 validation)
   - No test doubles or production policy decorators in this round (Phase 2.4 scope)
6. **Update CI for validation branch**: Restore or create minimal CI workflow that (a) restores + builds proposal tree against public core submodule, (b) verifies tree equivalence proof artifact
7. **Run CI on validation branch**: All checks pass (build succeeds, no unexpected errors)
8. **Prove tree equivalence**: Generate hash of proposed tree (commit SHA + tree SHA + all file SHAs), compare against validation branch HEAD — document discrepancies if any (should be zero)
9. **Prepare cutover artifacts**:
   - New orphan root commit (tree = validated tree, message = "Desktop: Fresh history rebuilding Trackdub-gated, consuming public core via submodule", author = same as Phase 1 commits)
   - Proposed root tree SHA (hash of tree object)
   - Literal force-with-lease command: `git push --force-with-lease=main:de1e5e86... origin main` (main @ old SHA as safety check)
   - Rollback branch name: `phase-2/gated-rebuild-rollback` (pointer to current main, created before cutover)
10. **Present for approval**: Green PR URL (validation branch → main), all workflow conclusions, old main SHA (`de1e5e86`), validated tree SHA, proposed root commit SHA, proposed root tree SHA, literal force-with-lease command, rollback branch confirmation. **STOP HERE — DO NOT EXECUTE FORCE-PUSH WITHOUT EXPLICIT APPROVAL.**
11. **Create rollback branch**: `phase-2/gated-rebuild-rollback` → current main SHA (before cutover)
12. **Execute force-push cutover**: `git push --force-with-lease=main:de1e5e86... origin main` (replaces main with new orphan root + all subsequent validated commits)
13. **Verify cutover**: Fetch updated main, confirm HEAD = new orphan root SHA, confirm `external/Trackdub` submodule reference is correct, clone fresh and verify build succeeds
14. **Delete validation branch**: `git push origin --delete phase-2/gated-rebuild-validation` (archive preserved in PR, can be recreated from commit record if needed)
15. **Post-cutover CI**: Run main branch CI (should be identical to validation branch results, proving cutover was clean)

**Approval gate (Step 10)**: Transition from **validation branch (non-destructive, reversible, reviewable via PR)** to **force-push cutover (irreversible against canonical URL, but protected by rollback branch reference)**. Requires explicit user confirmation of: (a) validation PR is green, (b) tree equivalence proven, (c) literal force-with-lease command has been reviewed, (d) rollback procedure understood.

**Rollback procedure (if cutover fails)**: `git push --force-with-lease=main:<cutover-sha> origin phase-2/gated-rebuild-rollback:main` (restores original main from pre-cutover snapshot). Requires force-with-lease protection and rollback-branch reference to succeed; both verified in steps 8–11.

## Explicitly NOT in this round (needs a separate go-ahead after Phase 2.3 validation)

- The force-with-lease cutover itself (Step 12) — executed only after explicit user approval at Step 10
- Moving `DesktopExportTierGate`/`DesktopLicensingComposition` or writing desktop-owned test doubles (Phase 2.4).
- Any legal/EULA/native-notice work (Phase 2.5) — also explicitly gated behind an approved consumer EULA per the original handoff's stop conditions, which was never granted.
- Cutting the "immutable core pin" (annotated tag) on `Trackdub` — the Phase 1 plan treats this as its own final publish-readiness step, not yet done.

## Verification

**Phase 2.1 (Archive Preservation)**:
- After creating the archive mirror, confirm via `git log --oneline` (local clone) that all 10 original commits and the exact same tip SHA (`b0ef90d8`) are present in `Trackdub-gated-Staging-Archive`, and that `Trackdub-gated` itself is completely untouched (`get_commit` on `Trackdub-gated`'s `main` still shows `b0ef90d8` as HEAD).

**Phase 2.2 (Manifest)**:
- Manifest correctness: cross-check every `src/`/`tests/`/`services/` directory currently in `Trackdub-gated` appears exactly once in the manifest with a destination assigned — no path silently dropped or double-counted.
- For the 15 shared-name project diffs: report file-count and any content differences found per project, not just "looks the same" — this is the load-bearing due-diligence step for Phase 2.2, so it needs to be a real diff, not an assumption.

**Phase 2.3 (Pre-Cutover Validation)**:
- Validation branch (`phase-2/gated-rebuild-validation`) exists with shared history (parent = old main)
- CI workflow on validation branch runs successfully (all checks green)
- Tree equivalence proof: computed tree hash matches validation branch HEAD (commit SHA + tree SHA + all file SHAs documented)
- Proposed orphan root commit SHA, tree SHA, and force-with-lease command are explicitly documented and reviewed
- Rollback branch (`phase-2/gated-rebuild-rollback`) pointer to current main created and verified before cutover
- Presentation artifact contains: green PR URL, all workflow conclusions, old main SHA, validated tree SHA, proposed root commit SHA, proposed root tree SHA, literal force-with-lease command, rollback confirmation
- **APPROVAL GATE**: User explicitly confirms all above before Step 12 execution
