# Missing Proof

[← Back to index](README.md)

Everything below is **not verified** — this audit was static, read-only, with no builds, no test runs, and no external-system calls.

1. **No build or test execution.** No `dotnet build/test/format`, no `npm run typecheck`, no `bun run build`, no `wrangler` call. Every "Confirmed" claim is code-and-wiring evidence only.

2. **Desktop end-to-end run.** Never executed a real media-import → ASR → TTS → export session. The reporting chain is traced; actual artifact correctness, watermark burn-in, and loudness normalization are unproven in this pass.

3. **Deployed API state.** Which worker version is live, which D1 migrations from `drizzle/` and `drizzle-activation/` were actually applied, whether `dubs` tables exist only in the deployed DB — unknown. Resolve with `wrangler deployments list` and `wrangler d1 migrations list`.

4. **Production signing key ↔ desktop public key match.** Whether `trackdub-prod-2026-08b`/`-2026-07-30` in the shipped ring correspond to the key configured in the production activation worker is unverified. Resolve by comparing `kid` in a real production token to the ring file inside a packaged release.

5. **Shipped trust-ring contents.** The staging-key-in-production-ring finding rests on an **uncommitted local file**. Resolve by unpacking a release zip from the `release.yml` artifact and reading `trackdub.trust.json`.

6. **Revocation feed freshness.** `ProductionLicensingBootstrap`/`DesktopLicensingComposition` require a signed, unexpired feed at startup or the app throws — but the installed feed's expiry date and whether any release has gone stale in the field were not inspected. Also unproven: the distribution path, since `release.yml` only uploads a zip artifact.

7. **Edge/WAF behavior in front of the API.** Rate limiting on `/api/auth/*` and the activation routes, bot protection, and whether `SameSite=Lax` host-only cookies behave as intended across the real portal/API origins. Resolve with browser DevTools on a staging deploy.

8. **Live update-check outcome.** `ReleaseManifestUpdateService` swallows all failures into `NoUpdate`, so a 404 and a network block are indistinguishable from the outside. Resolve by capturing the desktop's request to `api.trackdub.com/releases/manifest.json`.

9. **Third-party cloud lane.** The ElevenLabs dubbing path was never traced (delegated agent failed on credit limits). Gemini `gemini-1.5-*` availability, Google TTS `LINEAR16` acceptance, and each adapter's key/quotas are unproven — a token present in a developer environment is not evidence of a supported production lane.

10. **Concurrency and races.** The seat release/claim window and non-atomic provisioning are read from code; the actual isolation level of D1 serialized reads/writes under concurrent activation calls was not exercised. Resolve with a concurrent activation test against a staging D1.

11. **`ExportStageHandler` resolution on headless paths.** Core registers the handler (`CompositionRoot.cs:495`) but no `IExportTierGate`; whether DI supplies the `null` default or throws is inference from MS DI's default-parameter handling, not an observation. Resolve with `dotnet run --project src/Trackdub.Cli -- dub ...` through an export stage.
