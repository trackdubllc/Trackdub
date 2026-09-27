# Contract Drift and Configuration Mismatches

[← Back to index](README.md)

| Contract | Producer says | Consumer says | Effect |
|---|---|---|---|
| Password reset path | portal `/api/auth/forget-password` | better-auth `/request-password-reset` | 404 |
| Reset response shape | portal expects `{ok:true}` | server returns `{status:true}` | false failure even if routed |
| Dubbing job API | portal `schema.d.ts` 4 ops + `/api/dubs/` trailing slash | API: no routes, no `dubs` tables | whole feature 404 |
| Update manifest | core polls `/releases/manifest.json`, `-preview.json` | API serves neither; gated CI only uploads artifacts | silent, permanent "up to date" |
| License tier | API `z.string().min(1).max(50)`, default `"pro"` | core `claims.Tier.Equals("pro")` only | any other tier => Free (fail-closed but silently downgraded) |
| Seat count | API mints up to 50 machines | `LicenseService` reports `maxMachines: 2` unconditionally | UI/flow cap disagrees with token |
| `iss` / `aud` | activation worker sets distinct issuer/audience per env | parser drops them; ring validation ignores them | no audience isolation between envs |
| Language set | portal fallback + `placeholderData` include `zh` | API language list excludes Chinese | user picks a language the backend rejects |
| API origin | `lib/config.ts` production default | `features/jobs/*` same-origin default | split-host auth/upload |
| Stage prerequisite set | engine `{Vad,Asr,Translation,Tts}` | shell shows Separation/Diarization as upstream of ASR | failure cascade != user expectation |
| Exit semantics | engine: `PartialSuccess` is a distinct truth | CLI: `PartialSuccess` in success set | automation blind spot |
| Env-var naming | workers: `SIGNING_KEY_ID`, `ACTIVATION_ISSUER`, `ACTIVATION_AUDIENCE`, `ALLOW_DEV_LICENSES` (staging jsonc) | ring file: per-key `Issuer`/`Audience` left `null` | handoff configured one side only |
| Model readiness | orchestrator `Installed/Ready` | planner re-checks manifest hash but not `IntegrityFailed` | three different notions of "ready" |
| Trust-ring key validity | `trackdub-prod-2026-08` 1-second window | ring honors `NotBefore`/`NotAfter` | key is unselectable in practice |
| CI lock strictness | gated Linux job omits `--locked-mode` (documented at `ci.yml:69-73`) | `RestorePackagesWithLockFile=true` | non-reproducible Linux legs |
| Test env fidelity | activation tests force dev licenses | production policy decorator | prod path untested |
