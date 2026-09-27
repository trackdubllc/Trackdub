# Findings, Ordered by Priority

[← Back to index](README.md)

| # | Sev | Confidence | Repo : path : symbol | Stage | One-line failure |
|---|---|---|---|---|---|
| C-1 | Critical | Confirmed | portal `src/api/schema.d.ts:75-128`, `features/jobs/CreateJobPage.tsx:97`, `useJobs.ts:144-217`, `FileDropZone.tsx:184` ↔ api `src/index.ts:34-62`, `src/db/schema.ts:8` | Hosted dubbing | Every portal job action 404s; no `dubs` tables exist |
| C-2 | Critical | Confirmed | core `src/Trackdub.Cli/Handlers/RunPipelineHandler.cs:65-83`; `Program.cs:12,18`; `DubbingPipelineEngine.cs:2505-2550` | Reporting | Failed ASR/TTS run with no export exits 0 |
| C-3 | Critical | Confirmed | portal `src/lib/auth/auth-service.ts:24-49` ↔ better-auth `password.mjs:20`; api `src/auth/index.ts:30-58` | Account recovery | Reset is unreachable (wrong path) **and** never mailed (log-only, redacted in prod) |
| H-1 | High | Confirmed | api `src/auth/index.ts:~45-55`; `src/middleware/logging.ts:9-19` | Account recovery | Reset token travels in the URL path and is written to worker logs |
| H-2 | High | Strong evidence (config uncommitted) | gated `.../Licensing/production/trackdub.trust.json`, `TrustRingConfiguration.cs:27-96`, `DesktopLicenseSignatureTrustStore.cs:46-83`; core `LicenseTokenParser.cs:22-56`; api `wrangler.activation.staging.jsonc:17-25` | Licensing | Staging-signed token satisfies the production ring; `iss`/`aud` unchecked |
| H-3 | High | Confirmed | core `src/Trackdub.Composition/Runtime/ModelDownloadOrchestrator.cs:82-102`; `src/Trackdub.Inference/Runtime/Planning/RuntimePlanFactory.cs:779-800`, `:821-827`; `CompositeModelCacheInventory.cs:21-31` | Runtime planning / preflight | Corrupt-but-present model file is treated as installed *and* selectable |
| H-4 | High | Confirmed | core `DubbingPipelineEngine.cs:341-416`, `:932-1044`; `DubbingPipelineStages.cs` `PrerequisiteStages` | Separation / all | Transient failures are terminal (no retry); Separation failure doesn't gate ASR |
| H-5 | High | Confirmed | portal `src/lib/config.ts:7-8` vs `features/jobs/CreateJobPage.tsx:9`, `JobDetailPage.tsx:64-68`; `src/lib/auth/ensure-session.ts:16-22`; `auth-service.ts:24-49` | Portal auth/upload | Two API origins; any session error memoized as anonymous → silent 401 uploads |
| H-6 | High | Confirmed | portal (no tests/CI tracked); api `vitest.config.ts:50`; `vitest.activation.config.ts:30-38` | Evidence quality | Green suites cannot be trusted as proof; licensing tests never exercise prod policy |
| M-1 | Medium | Confirmed | api `src/activation/routes/admin.ts:24-73` ↔ core `LicenseService.cs:129-131`, `:124,:139` | Licensing | Tier vocabulary unbounded on mint, only `"pro"` honored on check; `maxMachines` minted up to 50 but hardcoded `2` in the result |
| M-2 | Medium | Confirmed | core `ReleaseManifestUpdateService.cs:12,28-76`; gated `.github/workflows/release.yml:39-83`; api `src/index.ts` | Updates | Update check permanently `NoUpdate` (all failures swallowed); releases have no distribution endpoint |
| M-3 | Medium | Confirmed | api `src/auth/provision.ts:26-49` | Sign-up provisioning | `createUser` then `linkAccount` non-atomic → orphan user rows |
| M-4 | Medium | Confirmed | api `src/activation/lib/seats.ts:141-179`; `routes/reactivate.ts:49-101` | Activation seats | Release-then-claim window allows seat inflation; token signed after mutation with no compensating delete |
| M-5 | Medium | Confirmed | gated `.../Licensing/DesktopExportTierGate.cs:53-67` | Export gate | Sync-over-async on the export path; failed init re-blocks every later export |
| M-6 | Medium | Needs runtime verification | core `GeminiCloudTranslationEngine.cs:18`, `GeminiCloudTranscriptionEngine.cs:21` | Cloud ASR/translation | Pinned `gemini-1.5-*` IDs on a deprecated upstream lane |
| M-7 | Medium | Confirmed | portal `src/features/dashboard/DashboardPage.tsx:20-50` | Dashboard | `isLoading` swallows `hasError` → permanent skeletons |
| M-8 | Medium | Confirmed | portal `src/api/hooks/useLanguages.ts:21-34,70-87` ↔ api `src/lib/languages.ts` | Language selection | Fallback/placeholder list offers `zh`, which the server rejects |
| L-1 | Low | Confirmed | core `src/Trackdub.Infrastructure/Tts/GoogleCloudTtsEngine.cs:137-160` | TTS | WAV header bytes counted as PCM samples in `durationSamples` |
| L-2 | Low | Confirmed | api `src/routes/health.ts:40-57`; `src/index.ts:34-62` | Observability | 404 envelope shape differs from every error envelope; health advertises capability nobody gates on |
| L-3 | Low | Confirmed | gated `trackdub.trust.json` (`trackdub-prod-2026-08`, 1-second window); gated `ci.yml:69-73` | Licensing / CI | Near-zero-width key validity; Linux CI deliberately omits `--locked-mode` |

---

## Detailed Critical and High findings

### C-1 — Portal dubbing jobs call an API that doesn't exist

*Impact:* any user clicking "create dub job" in the portal gets a 404/Hono nested error; uploads, listing, detail, and download are all dead. Marketing surface promises a hosted pipeline the backend never implemented.

*Root cause:* `schema.d.ts` was authored from a planned spec, not from the deployed worker, and nothing in either repo gates on that divergence (portal has no CI; API has no contract test).

*Remediation (targeted, not a rewrite):* either ship the `/api/dubs` routes + `dubs` tables, or remove/hide the Jobs feature behind the existing capability signal from `health.ts` (`availablePipelines`). The honest minimum is: add a portal-side startup capability check that consumes `GET /health` and disables job creation when `dubs` isn't advertised, and delete `CreateJobPage.tsx:97`'s trailing slash (`/api/dubs/` vs the declared `/api/dubs`).

*Verify:* `grep -rn "'/api/dubs" api.trackdub/src` returns nothing today; after the fix, `curl -s $API/health | jq .availablePipelines` and one real `POST /api/dubs` with a staging session.

### C-2 — CLI exits success on a failed pipeline

*Impact:* CI, SDK automation, and any shell script wrapping `trackdub dub` will treat an ASR/TTS-failed run as a dubbing success, produce no artifact, and — because `RunManifestWriter.WriteAsync` runs unconditionally at `:59-63` — also write a manifest into the project dir that looks authoritative.

*Root cause:* `OverallStatus == PartialSuccess` conflates "some stages succeeded" with "the run achieved the user's goal." A failed prerequisite plus earlier successes *is* `PartialSuccess` (`:2505-2550`), because downstream stages become benign skips rather than failures.

*Remediation:* in `RunPipelineHandler`, exit success only when `OverallStatus == Succeeded`, **or** when `PartialSuccess` and every requested stage in `options.StageFilter` is Succeeded/Skipped-benign **and** `ExportedFilePath is not null` for an export-bearing filter; otherwise `ExitPipelineFailure` with a `PartialFailure` error code. Keep `PartialSuccess` in the JSON payload — only the exit code changes.

*Verify:* new xUnit case in the CLI test project asserting a `Succeeded + Failed(Asr)` result exits non-zero; existing tests don't touch this branch.

### C-3 — Account recovery is doubly broken

*Impact:* a user who forgets their password cannot recover the account at all, in any environment where logs aren't being read by a human.

*Root cause:* (a) portal guessed a REST-ish path instead of better-auth's; (b) the API was never given a mail transport, and its placeholder intentionally withholds the link in production.

*Remediation:* point `auth-service.forgotPassword` at `/api/auth/request-password-reset` and accept better-auth's `{status:true}` shape rather than the hand-typed `{ok:true}`; implement `sendResetPassword` against a real provider (Resend is already used by `trackdub.com`) with the reset URL delivered **in the request body to the client-side route, not in a log line**. Also fix `auth-service.getSession`'s collapse of 401/403/network to `null` (see H-5).

*Verify:* one browser-driven reset on staging: request → receive email → open → land on `/reset-password/:token` → session established.

### H-1 — Reset token in logs

*Root cause:* better-auth's default URL embeds the token in the path and the API's logging middleware logs `new URL(c.req.url).pathname`. Even after C-3's fix, whoever reads the reset page writes the credential into worker logs.

*Remediation:* redact `reset-password` path segments in `logging.ts`, or configure better-auth to deliver the token in the body only.

*Verify:* staging log tail shows `***` for tokenized paths.

### H-2 — Staging key inside the production ring

*Impact:* a token minted by `activate.trackdub.dev` with `ALLOW_DEV_LICENSES: "true"`, issuer `https://activate.trackdub.dev`, audience `trackdub-license-staging`, unlocks Pro on a production-built desktop — provided the ring file ships as it currently reads.

*Root cause:* `TrustRingConfiguration.cs:27-96` only rejects keys flagged `IsDevelopmentOnly`, and validation never compares `iss`/`aud`; the parser discards them, so audience isolation is structurally impossible.

*Remediation, in order:* (1) remove the staging key from the production ring file, or set `IsDevelopmentOnly: true`; (2) bind each ring key to the required `Issuer`/`Audience` (the fields already exist in `TrustRingConfiguration.cs:189-197` and are `null` today) and verify them in `DesktopLicenseSignatureTrustStore` when non-null; (3) add `iss`/`aud` to `LicenseTokenParser.PayloadDto` so core can enforce audience rather than only signature.

*Confidence:* the trust-ring file is uncommitted local state — **this is not proof that a shipped build contains the staging key.**

*Verify:* `unzip -p <release>.zip trackdub.trust.json | jq '.keys[] | {keyId,isDevelopmentOnly,issuer}'` against a real packaged artifact, plus a negative test signing a token with the staging key and asserting a production-ring `Verify` rejection.

### H-3 — Model cache: install-time hashing, use-time blindness

*Impact:* a truncated or tampered model file that was once verified and later damaged (disk error, partial delete, manual edit) still resolves as an installable identity and is selected for inference; failures then appear as garbage output or an opaque ORT error rather than "download required."

*Root cause:* two independent no-verify shortcuts: the orchestrator's early return at `:82-102` (state + file presence only), and `RuntimePlanFactory.cs:779-800`, which honors `LocalIntegrityFailed` only in the optimized-variant branch (`:821-827`). Manifest-vs-cache hash divergence *is* caught (`:875-894`, `ModelIntegrityMismatch`), which is exactly why each side's unit test passes.

*Remediation:* in the ordinary cache loop, treat `cacheRecord.IntegrityFailed` as `DownloadRequired` (mirroring `ModelInventoryService.DetermineState:619-668`, which already gets this right); in `ModelDownloadOrchestrator`, re-verify sha256 for a present file whenever the manifest supplies a non-empty expected hash, and mark `IntegrityFailed` on mismatch instead of returning "up to date." Small, local, and both existing tests stay valid.

*Verify:* add the missing joint test — same model, manifest hash, cache record present-file-but-wrong-content => planner must report DownloadRequired and the orchestrator must re-download.

### H-4 — No retry, and separation isn't a prerequisite

*Impact:* one transient ONNX/network hiccup ends the run and requires a manual re-run; a failed stem separation leaves cleanup/ASR operating on the mixed spine, and nothing in the status line says the transcript was derived from un-separated audio.

*Remediation:* (a) bounded retry (1-2 attempts, exponential backoff) inside `ExecuteStageAsync` for `TransientFailureClassifier.IsTransient` exceptions only, with attempts recorded on the `StageOutcome`; (b) add `Separation` to `PrerequisiteStages`, or emit an explicit degradation record when separation failed and a downstream stage consumed the original audio. Note that (b) changes skip cascades, so `StageSkipReasonCodes.PrerequisiteFailed` handling and `PipelineRunResultDescriber.ResolveCandidateStageNames` groups need a look together.

*Verify:* fake stage that throws a transient exception twice then succeeds; assert one `Succeeded` outcome and attempts == 2.

### H-5 — Portal origin split and error-swallowing sessions

*Impact:* on any deployment where the portal isn't same-origin with the API, auth calls go to `https://api.trackdub.com` (`lib/config.ts:7-8`) while upload/create/download go to the portal's own origin (`?? ""`), so `credentials: "include"` carries cookies to one host and fetches hit another => consistent 401 or CORS failure that reads as "not logged in". Layered on top, `getSession` maps 401, 403 and network failure to the same `null`, and `ensure-session.ts:16-22` memoizes that `null`, so a 3-second API blip makes the tab anonymous until reload.

*Remediation:* single `API_BASE_URL` sourced from `lib/config.ts` everywhere; distinguish "known anonymous (401)" from "unknown (error)" in `auth-service.getSession`; don't cache a rejection.

*Verify:* staging with distinct origins, watch cookie `Domain`/`SameSite=Lax` behavior; a failing `/api/auth/get-session` must surface as an error state, not as signed-out UI.

### H-6 — The evidence layer can't support the claims

portal has no tests and no workflow; api's `vitest.config.ts:50` sets `dangerouslyIgnoreUnhandledErrors: true`; `vitest.activation.config.ts:30-38` forces `APP_ENV: "development"` and `ALLOW_DEV_LICENSES: "true"`. So: the two repos holding the broken cross-repo contracts are precisely the two with no enforcement, and the activation suite never runs production policy.

*Remediation:* remove `dangerouslyIgnoreUnhandledErrors`; add an `APP_ENV=production` / `ALLOW_DEV_LICENSES=false` activation suite (dev-token mint must be refused); add portal typecheck+build+one route-contract test to CI; add a contract test that asserts every operation in portal's `schema.d.ts` resolves against the API's Hono route list.
