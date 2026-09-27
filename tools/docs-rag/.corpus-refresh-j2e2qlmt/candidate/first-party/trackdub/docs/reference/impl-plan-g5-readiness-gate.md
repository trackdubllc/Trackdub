# Implementation Plan — G5: Consolidated Pipeline Readiness Gate

**Source:** [design-g5-readiness-gate.md](design-g5-readiness-gate.md)

---

## Phase 1: Contracts & core models (2 days)

Establish the read-only types and service interface. Map ReadinessState enum directly to Spec §5 (11 distinct states). Extend RunReadinessSnapshot with frozen RuntimeModelSelections.

**Files:**
- src/Trackdub.Contracts/Pipeline/ReadinessState.cs
- src/Trackdub.Contracts/Pipeline/StageReadiness.cs
- src/Trackdub.Contracts/Pipeline/IPipelineReadinessService.cs

---

## Phase 2: Application layer — Evaluate (3 days)

Build PipelineReadinessService. Evaluate per stage: artifact resumability, download/import/blocked status, cloud-key presence, voice-clone consent. Cache by (stage, selection-hash, artifact-fingerprint).

---

## Phase 3: Application layer — Provision (2 days)

Extend RuntimeModelSetupCoordinator. Batch DownloadRequired/ImportRequired stages by (ProviderKey, ModelId). Call RuntimeModelSetupWorkflow once. Demote per-stage Ensure* to non-interactive assert.

---

## Phase 4: SDK — pre-flight + Provision front-load (3 days)

Move provisioning fully up front in TrackdubDubbingEngine.RunPreFlightChecksAsync. Auto-download eligible stages. Fail fast with aggregated error listing all unmet stages. Delete stageProvisionedDuringExecution branch.

---

## Phase 5: App — readiness panel + live re-validation (4 days)

Build RunConfigPanelViewModel. Bind to draft selections. Debounce tier/lang/voice changes (300ms). Re-evaluate only affected stages (cache). Update per-stage badges live. Pre-run backstop: refuse Run while any stage is blocking.

---

## Phase 6: App — demote per-stage Ensure* (1 day)

Remove dialog calls from stage runners. Replace with non-interactive assert. Gate prevents reaching this assert path.

---

## Phase 7: Cleanup — diarization mismatch (1 day)

Verify SpeakerDiarizationStage calls CreateRuntimeSelections(snapshot), not CreateDefaultRuntimeSelections(). Both provision and execute see same snapshot.

---

## Risks

- Evaluate cost (disk + EP probes): mitigated by debounce + cache. Watch stale-cache if artifact store mutated outside context.
- SDK behavior change (longer pre-flight, fail-fast): confirm with CLI/API consumers.
- Cloud key validity: default to "present"; validate on explicit user action only.
