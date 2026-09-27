# Executive Summary

[← Back to index](README.md)

**Overall health: the desktop pipeline is the healthy part of this system; the cloud half is largely aspirational, and the automation surface lies about success.**

Trackdub today is one real product (local desktop + headless engine) wearing the costume of two. `Trackdub` (core) and `Trackdub-gated` (Avalonia shell) form a genuinely traced, honest chain: shell → `DubbingSessionOptions` → `DubbingPipelineEngine` → stage handlers → artifacts → `DubbingRunResult` → shell status text. `portal.trackdub` and `api.trackdub` do **not** form a working dubbing pipeline — the portal's entire hosted-jobs UI calls endpoints the API never registers, and the API implements only auth/activation/admin. Nothing was modified, committed, or deployed.

## Top five breakpoints

1. **Critical — Portal's hosted dubbing flow is unreachable.** `portal.trackdub` performs `POST /api/dubs`, `/api/dubs/upload`, `GET/DELETE /api/dubs/{jobId}`, `/api/dubs/{jobId}/download` (declared in its generated `src/api/schema.d.ts:75-128`); `api.trackdub` registers none of them (`src/index.ts:34-62`), and `src/db/schema.ts:8` states the `dubs` tables were never added. **Confirmed in code.**
2. **Critical — CLI reports pipeline failure as exit 0.** `RunPipelineHandler.cs:65-83` treats `PartialSuccess` as success; a run where ASR or TTS hard-fails and nothing exports still exits `Program.ExitSuccess` (`Program.cs:12`) with `ExportedFilePath: null`. **Confirmed in code; no test asserts this path.**
3. **Critical — Password recovery cannot work in production**, for two independent reasons: the portal calls `/api/auth/forget-password` (`auth-service.ts:24-49`) while better-auth's real route is `/request-password-reset`; and the API's `sendResetPassword` never sends mail — it logs, and strips `resetUrl`/`token` when `APP_ENV=production` (`src/auth/index.ts:30-58`). **Confirmed in code.**
4. **High — Staging-minted license tokens can be accepted by the production desktop ring.** `Trackdub-gated/.../Licensing/production/trackdub.trust.json` (uncommitted) carries `trackdub-staging-2026-09` with `IsDevelopmentOnly: false`, which is exactly the `SIGNING_KEY_ID` the staging activation worker signs with (`api.trackdub/wrangler.activation.staging.jsonc:17-25`) — the opposite of that file's own comment. `iss`/`aud` are never verified anywhere (`LicenseTokenParser.cs:22-56` drops them; `DesktopLicenseSignatureTrustStore.cs:46-83` never checks them). **Strong evidence; production packaging unverified.**
5. **High — Model integrity is checked on download but not on use.** `ModelDownloadOrchestrator.cs:82-102` returns "installed, no download needed" purely from record state + file presence, and `RuntimePlanFactory.cs:779-800` ignores `cacheRecord.IntegrityFailed` for ordinary cache records. **Confirmed in code.**

## Confirmed vs risky/unverified

**Confirmed by tracing:** findings 1-5, the `/api/dubs` and `forget-password` contract breaks, absent engine-level retry (`DubbingPipelineEngine.cs:932-1044`), desktop result reporting being *honest* (`PipelineRunResultDescriber.cs`), the export-tier-gate wiring being null-safe (`ExportStageHandler.cs:81-88` — the earlier NRE concern is resolved; it is guarded, and `LicenseService.cs:129-131` fails closed to `Free`).

**Not verified — no builds, no tests, no runtime, no deployed-system inspection:** everything in [Missing Proof](05-missing-proof.md), most consequentially whether any staging-minted token has ever reached a production ring, and whether the desktop app actually completes media → export end to end.

## Process note

Independent subagent review was **not** performed for the core-pipeline, desktop-handoff, and direct-cloud lanes. All three `feature-dev:code-reviewer` dispatches failed with "You've reached your credit usage limit." Those lanes were traced inline instead; the ElevenLabs direct-cloud lane was not re-traced at all and is reported as untraced rather than as clean.
