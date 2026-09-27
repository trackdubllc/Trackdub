# Four-Repository Architecture Audit — 2026-09-19

**Scope:** `Trackdub` (public core), `Trackdub-gated` (private Avalonia shell), `portal.trackdub` (frontend), `api.trackdub` (Cloudflare Workers API).  
**Method:** Static, read-only, code-and-wiring evidence only. No builds, no tests, no runtime, no external-system calls.  
**Status:** Independent subagent review was **not** performed for core-pipeline, desktop-handoff, and direct-cloud lanes (delegated agents failed on credit limits). Those lanes were traced inline instead; the ElevenLabs direct-cloud lane was not re-traced at all and is reported as untraced rather than as clean.

## Sections

1. [Executive Summary](00-executive-summary.md) — Overall health, top five breakpoints, confirmed vs risky/unverified
2. [System Map](01-system-map.md) — Repository responsibilities, cross-repo contract map, Mermaid topology diagram
3. [End-to-End Pipeline Trace](02-pipeline-trace.md) — Stage-by-stage trace of the local dubbing run
4. [Findings, Ordered by Priority](03-findings.md) — Findings table + detailed Critical/High entries
5. [Contract Drift and Configuration Mismatches](04-contract-drift.md) — Producer/consumer contract drift table
6. [Missing Proof](05-missing-proof.md) — Everything not verified (no builds, no tests, no runtime)
7. [Remediation Plan](06-remediation-plan.md) — Four remediation groups with files, impact, risk, verification
8. [Recommended Execution Order](07-execution-order.md) — Seven execution packages with dependencies

## Quick links to top findings

- **C-1:** Portal hosted-dubbing Jobs flow targets `/api/dubs*` endpoints that do not exist in api.trackdub
- **C-2:** CLI `run-pipeline` exits 0 on PartialSuccess
- **C-3:** Password recovery impossible in production (two independent breakages)
- **H-1:** Reset tokens in URL path and logging middleware
- **H-2:** Staging signing key present in production desktop trust ring
- **H-3:** Model cache install-vs-plan coherence gap
- **H-4:** No engine-level retry for transient failures; Separation not in PrerequisiteStages
- **H-5:** Portal API base URL split + error-swallowing sessions
- **H-6:** No tests/CI in portal; api vitest `dangerouslyIgnoreUnhandledErrors: true`
