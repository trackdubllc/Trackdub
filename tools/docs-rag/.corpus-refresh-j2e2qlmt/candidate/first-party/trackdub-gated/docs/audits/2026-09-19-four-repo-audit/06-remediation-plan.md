# Remediation Plan

[← Back to index](README.md)

## 1. Stop the bleeding (no architecture change)

| Change | Files / repo | Impact | Risk | Verification |
|---|---|---|---|---|
| CLI: non-zero exit on goal-missing `PartialSuccess` | core `src/Trackdub.Cli/Handlers/RunPipelineHandler.cs:65-83` | Removes false automation success | Low — scripts depending on exit 0 must be re-checked | New CLI test: `Succeeded + Failed(Asr)` => exit != 0 |
| Remove staging key from production ring (or flag dev-only) | gated `src/Trackdub.App.Avalonia/Licensing/production/trackdub.trust.json` | Closes cross-environment license acceptance | Low; ensure no legitimate staging tokens are trusted in prod by accident | Unpack release, `jq '.keys[].keyId'`; negative-sign test |
| Portal: take API origin from one place | portal `src/features/jobs/CreateJobPage.tsx:9`, `JobDetailPage.tsx:64-68`, `FileDropZone.tsx:184` => `src/lib/config.ts:7-8` | Fixes split-host 401s | Low | Staging origins differ, then run upload + create + download |
| Portal: stop caching failures as anonymous | portal `src/lib/auth/ensure-session.ts:16-22`, `auth-service.ts:24-49` | Transient outage no longer logs users out | Low | Kill API for 3s, confirm error state not signed-out |
| Redact reset token from logs | api `src/middleware/logging.ts:9-19` | Credential stops entering logs | Low | Staging log tail |
| Fix reset endpoint + shape | portal `auth-service.ts` `forgotPassword` | Restores reachable route | Low | Network tab shows 200 `{status:true}` |
| Disable hosted-jobs UI when capability absent | portal `features/jobs/*` + api `src/routes/health.ts:40-57` | Users aren't handed a 404 surface | Low | `GET /health` without `dubs` => CTA disabled |

## 2. Contracts and orchestration

| Change | Files / repo | Impact | Risk | Verification |
|---|---|---|---|---|
| Enforce audience on license keys: add `iss`/`aud` to payload, verify when non-null | core `src/Trackdub.Licensing/LicenseTokenParser.cs:22-56`; gated `DesktopLicenseSignatureTrustStore.cs:46-83`, `TrustRingConfiguration.cs:189-197` | Structural env isolation | Medium — breaks any token lacking the claims; gate behind ring-key config | Positive + wrong-audience negative tests |
| Pin tier + seat vocabulary | api `src/activation/routes/admin.ts:24-73` (`z.enum(["free","pro"])`, seat cap <= policy max); core `LicenseService.cs:129-131,:139` | No silent downgrade, honest max | Low | Mint `"enterprise"` => 400 at the API, not Free at the client |
| Route-contract test: portal `schema.d.ts` subset of API Hono routes | portal `src/api/schema.d.ts`; api `src/index.ts:34-62` | Kills C-1 class of drift permanently | Low | `bun run typecheck` / new test fails if a route is removed |
| Serve `/releases/manifest.json` **or** retire the poll | api `src/index.ts`; gated `.github/workflows/release.yml:39-83`; core `ReleaseManifestUpdateService.cs:12,28-76` | Real updates, or honest removal | Medium — publishing pipeline work | Dispatch a manifest, confirm desktop reports the version |
| Language set single-sourced | portal `useLanguages.ts:21-34,70-87`; api `src/lib/languages.ts` | Removes `zh` phantom | Low | Remove `zh` server-side => portal list follows |
| Make Separation a prerequisite **or** record degradation | core `DubbingPipelineStages.cs` `PrerequisiteStages`; `PipelineRunResultDescriber.cs` groups | Coherent failure semantics | Medium — changes skip cascades and row statuses | Tests for separation-failed => ASR-skipped-with-reason |

## 3. Reliability and observability

| Change | Files / repo | Impact | Risk | Verification |
|---|---|---|---|---|
| Bounded transient retry inside stage execution | core `DubbingPipelineEngine.cs:932-1044` | Infra blips stop ending runs | Medium — idempotency per stage; retry only `IsTransient` | Fake stage throwing transient x2 then success |
| Honor `IntegrityFailed` in the ordinary plan path; re-verify present-file hash on install check | core `RuntimePlanFactory.cs:779-800`; `ModelDownloadOrchestrator.cs:82-102` | Corrupt models become DownloadRequired | Low — extra hashing cost on install check only | Joint test: manifest hash + wrong-content file |
| Atomic provisioning | api `src/auth/provision.ts:26-49` | No orphan users | Low | Force `linkAccount` failure, assert no user row |
| Seat swap atomicity | api `src/activation/lib/seats.ts:141-179`, `routes/reactivate.ts:49-101` | No inflated seat counts | Medium — activation is money-adjacent; keep the guarded rollback | Concurrent reactivation against staging D1 |
| Async tier-gate init (drop sync-over-async, cache failure) | gated `DesktopExportTierGate.cs:53-67` | No blocked pool thread per export; no repeated failing init | Low | Time first export after a forced init failure |
| Surface, don't swallow, update-check and mail failures | core `ReleaseManifestUpdateService.cs:28-76`; api `src/auth/index.ts:30-58` | Silent `NoUpdate`/undelivered mail become visible | Low | Log line with the reason on each path |
| Real mail transport | api `src/auth/index.ts` `sendResetPassword` | Recovery works in prod | Low | End-to-end reset on staging |
| Per-run correlation id across shell => engine => stage-run rows => files | core engine + gated `PipelineStageExecutor.cs` | Supportable failures | Medium (cross-repo; core lands first) | One run, ids match in log and SQLite |

## 4. Tests, CI, deployment

| Change | Files / repo | Impact | Risk | Verification |
|---|---|---|---|---|
| Drop `dangerouslyIgnoreUnhandledErrors` | api `vitest.config.ts:50` | Real test signal | Low — may expose existing flake | `npm run test` |
| Add production-mode activation suite | api `vitest.activation.config.ts:30-38` | Proves prod policy path | Low | Dev token mint is refused |
| Portal CI: typecheck + build + contract test | portal (new workflow) | Drift caught pre-merge | Low | PR shows red on route removal |
| Reconcile core test coverage with C-2/H-3 gaps | core `tests/Trackdub.Application.Tests`, `Trackdub.Inference.Tests:891-913`, `Trackdub.Composition.Tests:546-572` | Tests cover the transitions, not the two halves | Low | New cases fail before the fixes |
| Decide Linux `--locked-mode` | gated `.github/workflows/ci.yml:69-73` | Reproducibility, or a documented exception | Low | Job passes either way |
| Publish release + revocation feed to a real endpoint | gated `release.yml:39-83`, `tools/release/package-release.ps1` | Updatable product, revocable keys | Medium — deployment surface | Fresh install checks the live channel |
