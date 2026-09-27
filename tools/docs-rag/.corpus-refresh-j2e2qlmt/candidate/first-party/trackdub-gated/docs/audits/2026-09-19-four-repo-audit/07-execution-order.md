# Recommended Execution Order

[← Back to index](README.md)

## Package A — stop the lying (core CLI + gated config). Land together. No dependencies.

1. CLI exit-code fix (`RunPipelineHandler.cs:65-83`) + its test.
2. Remove/flag the staging key in `production/trackdub.trust.json`.
3. Unpack a release artifact to establish whether item 2's exposure was ever shipped ([Missing Proof](05-missing-proof.md) items 4-5). Nothing else in this audit needs that answer; C-2, C-3 and H-3 do.

*Why first:* both are one-file, zero-architecture changes that currently make results and licenses untrustworthy, and A-3 converts H-2 from "strong evidence" into either a release incident or a closed concern.

## Package B — restore account recovery (portal + api). Land together: the portal path fix alone still doesn't send mail.

1. `/api/auth/request-password-reset` + `{status:true}` shape.
2. Real mail transport, token not logged.
3. Reset-path redaction in `logging.ts`.
4. `getSession`/`ensure-session` error-vs-anonymous split and the shared `API_BASE_URL`.

*Risk gate:* do not deploy the mail change without confirming Turnstile/secret wiring — `portal.trackdub`/`trackdub.com` AGENTS.md warn those values must stay out of the repo.

## Package C — model-cache coherence (core only). Independent of A and B.

1. `RuntimePlanFactory` honors `IntegrityFailed` in the ordinary loop.
2. `ModelDownloadOrchestrator` re-verifies present files when the manifest has a hash.
3. Add the joint test that both existing tests fail to cover.

*Order matters:* ship the test after 1+2 so it demonstrates the transition rather than pinning current behavior.

## Package D — pipeline semantics (core first, gated second; sequential, cross-repo).

1. Core: transient retry, `Separation` prerequisite/degradation decision, per-run correlation id — all inside `DubbingPipelineEngine.cs` + `DubbingPipelineStages.cs`.
2. Bump the core pin to an explicit SHA/tag, then re-check `PipelineRunResultDescriber.ResolveCandidateStageNames` against any new reason codes.
3. Gated: async tier-gate init.

*Why gated last:* `external/Trackdub` is read-only here; nothing in the shell can land before the core commit exists.

## Package E — the hosted product decision (portal + api). Serialize behind A-D, and gate on a product answer.

1. First: contract test proving `schema.d.ts` routes exist. It will fail, which is the point.
2. Then choose — implement `/api/dubs*` + `dubs` tables, or remove the Jobs feature and its schema entries. Prefer removal unless hosted dubbing is actually funded; the engine has no server-side execution model yet, and no artifact/R2 delivery lane exists in code.
3. In the same package: tier/seat vocabulary, atomic provisioning, seat atomicity, language single-sourcing, update manifest.

*Dependencies:* the update-manifest work needs the release-distribution decision from A-3; the tier vocabulary needs the ring audience work in Package F if higher tiers than `pro` are wanted.

## Package F — license hardening (core + gated, after A).

1. `iss`/`aud` in `LicenseTokenParser`.
2. Verify `iss`/`aud` in `DesktopLicenseSignatureTrustStore` when a ring key declares them; populate `Issuer`/`Audience` on the production and staging keys.
3. Negative tests: staging-signed token against production ring must be rejected twice over — by key absence *and* by audience.

*Why after A:* A removes the immediate exposure; F removes the structural ability to reintroduce it.

## Package G — evidence layer (portal + api CI), can run in parallel with C-F.

Drop `dangerouslyIgnoreUnhandledErrors`, add the production-mode activation suite, add portal typecheck/build/contract CI. Land earliest that doesn't touch runtime code; without G, packages E and F will re-drift silently.

---

**Explicitly not recommended:** a broad pipeline rewrite. Every confirmed core defect (C-2, H-3, H-4, M-5) is a local, targeted correction in one file, and the desktop reporting chain is already correct — rewriting orchestration would spend the one part of this system that demonstrably works.
