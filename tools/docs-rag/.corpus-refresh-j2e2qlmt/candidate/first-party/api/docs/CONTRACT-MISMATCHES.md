# Frontend contract mismatches

Differences discovered between the portal's expected contract
(`trackdub-api-hub`: `src/api/schema.d.ts`, `src/api/hooks/*`,
`src/lib/auth/*`) and what Better Auth / this Worker actually serve. Per the PR
brief, nothing was silently changed — each item below is either handled with the
smallest compatible fix (no frontend change) or flagged for a decision.

---

## 1. Session endpoint path — HANDLED (Worker alias)

- **Portal calls:** `GET /api/auth/session` (`src/lib/auth/auth-service.ts`).
- **Better Auth serves:** `GET /api/auth/get-session`.
- **Fix:** the Worker aliases `/api/auth/session` onto Better Auth's
  `get-session` handler (same response + cookie behavior). No frontend change.
  See `src/index.ts`. If preferred, the portal could call `get-session`
  directly and the alias removed.

## 2. CSRF requires an `Origin` header on POST — INTEGRATION REQUIREMENT

- Better Auth rejects state-changing POSTs without a trusted `Origin` header
  with `403 MISSING_OR_NULL_ORIGIN`.
- The portal's clients already send `credentials: "include"` on cross-origin
  requests, so the browser always attaches `Origin` — this works in production.
- **Action:** none needed, but do **not** strip `Origin` in any proxy, and keep
  `PORTAL_ORIGIN` exact. Documented so it isn't mistaken for a bug.

## 3. `GET /api/version` — not in portal contract

- Required by the PR brief; **not** present in the portal's OpenAPI schema and
  not called by any hook. Implemented anyway for deploy verification.
- **Action:** none. Harmless extra endpoint; wire the portal to it if desired.

## 4. `HealthResponse` has runtime-specific fields — RETURNED NULL

- The portal's `HealthResponse` includes `processId`, `uptime`, `memoryMb`
  (process-oriented, from the .NET desktop app's shape).
- The Workers runtime has no equivalent process id / uptime / RSS.
- **Fix:** these are returned as `null`; `status`, `timestamp`, `environment`,
  `runtimeStatus`, and `availablePipelines` are populated. If the portal needs
  real numbers here, the field set should be revised.

The response also exposes `capabilities.jobIntake`, `capabilities.upload`,
`capabilities.jobProcessing`, and `capabilities.outputDownload`. Management
readiness only means D1 contains the job schema. Upload and job creation are
accepted only when `DUB_PROCESSOR_ENABLED=true`; this flag must remain unset
until a real worker consumes queued jobs. `availablePipelines` separates
`dub-management` from `dub-processing` for the same reason.

## 5. `/api/languages` shape + coverage

- The endpoint is **not** in the portal's OpenAPI schema; it's a bespoke hook
  (`src/api/hooks/useLanguages.ts`) that accepts several envelopes and falls
  back to a static list.
- We return its documented preferred shape: `{ items: [{ code, name }] }`.
- **Coverage difference:** the portal's *fallback* list includes Chinese (`zh`)
  and a few others. The authoritative catalog we serve (mirroring the desktop
  app's real TTS coverage, `src/lib/languages.ts`) is **22 languages and
  excludes Chinese**, because it is disabled in the multilingual TTS model.
  Advertising `zh` would fake dubbing readiness.
- **Action:** portal fallback is cosmetic; the Worker list is authoritative.
  Align the portal fallback if exactness matters offline.
- **Auth:** endpoint is public (no session required) — populating a language
  dropdown shouldn't require signing in first.

## 6. Password-reset endpoint spelling — COMPATIBLE

- The portal posts to `/api/auth/forget-password` (`forget`, not `forgot`).
- Better Auth serves exactly `/api/auth/forget-password`. No change needed.
  (Better Auth also exposes `/api/auth/request-password-reset` as an alias.)

## 7. Email delivery not implemented (PR1)

- Forgot-password and invite flows generate tokens but PR1 ships **no email
  provider**. Reset links are logged (non-production only).
- **Action:** wire an email provider (e.g. Resend/MailChannels) in a later PR.
  Until then, seed the first user via the `password` bootstrap on
  `POST /api/admin/invite` (see README).

---

## Endpoints NOT implemented in PR1 (later PRs)

These exist in the portal contract but are deliberately out of scope for the
foundation PR: `/api/billing/*`, `/api/webhooks*`, `/api/keys*`.

`/api/dubs*` is now scaffolded (upload → create Queued job → list/get/cancel;
download returns 409 until a real pipeline marks Completed). Billing and
webhook/key surfaces still 404 until their PRs land.
