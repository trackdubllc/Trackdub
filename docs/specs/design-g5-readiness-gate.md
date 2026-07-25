# Design Spec — G5: Consolidated Pipeline Readiness Gate

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G5** — readiness is inconsistent: front-loaded at import, but post-import tier/diarization/voice changes interrupt mid-run; the headless path throws instead of prompting.

**Decisions (locked with product):**
1. Gate lives in **both** places — a Configure-time readiness panel **and** a pre-run backstop.
2. On selection change after provisioning — **live re-validate + badge** the affected stage.
3. Headless/SDK path — **unify** with the app fix via one shared service.

**Scope discipline:** this spec fixes **G5 only**. The readiness panel exposes a *hook* where per-stage cloud-key status will surface (G3), but cloud-egress consent/visibility behavior is **out of scope** and stays deferred.

---

## 1. Problem — the scatter, with evidence

Readiness today is resolved **inline, per stage, at each stage's trigger**. Each stage runner calls its own `Ensure*ModelAvailableAsync`, whose callbacks drive the modal decision dialog; `!IsReady` aborts the stage with a status message:

| Stage runner (`AvaloniaMainWindowViewModel.PipelineUi.cs`) | Provisioning call |
|---|---|
| `RunAsrStageAsync` (~1394–1432) | `EnsureImportModelsAvailableAsync` + `EnsureDiarizationModelAvailableAsync` (or `EnsureAsrModelAvailableAsync`) |
| `RunDiarizationStageAsync` (~1499) | `EnsureDiarizationModelAvailableAsync` |
| `RunTranslationStageAsync` (~1543) | `EnsureTranslationModelAvailableAsync` |
| `RunTtsStageAsync` (~1591) | `EnsureTtsModelAvailableAsync` (+ inline voice-clone consent) |
| `SegmentEdit.cs` (~364, ~412) | `EnsureTtsModelAvailableAsync` (per-segment regen) |

Three concrete defects fall out:

1. **Scatter.** As the user drives the staged workflow (transcribe → diarize → translate → TTS), each stage's first run can pop a setup dialog. There is no single point where "is this run ready end-to-end?" is answered.
2. **Selection drift.** Each runner rebuilds selections fresh (`CreateDefaultRuntimeSelections()`), so a model-tier change made *after* import causes the next stage's `Ensure*` to discover a new required model → a new mid-workflow dialog.
3. **Snapshot mismatch (latent bug).** `RunDiarizationStageAsync` **provisions** off `CreateDefaultRuntimeSelections()` (~1499) but **executes** off `CreateRuntimeSelections(stateSnapshot)` (~1511). The gate and the work can disagree on which model is in play — G5's bug in miniature. Any fix that evaluates one selection set and runs another simply relocates the defect.

The headless path (`TrackdubDubbingEngine.RunPreFlightChecksAsync`) already loops all stages once before execution, but it is **check-only**: auto-downloadable VAD/ASR/Diar models are explicitly *not* failed and instead download **mid-stage** (the `CanAutoDownload && stageProvisionedDuringExecution` branch, ~265–277). So the two paths are inconsistent and neither truly front-loads provisioning.

---

## 2. Goals / Non-goals

**Goals**
- One **readiness model** shared by the Configure panel, the pre-run backstop, and the headless gate.
- A run is evaluated, provisioned, and executed against **one immutable selection snapshot**.
- Selection edits live-re-evaluate and re-badge without blocking the user.
- Readiness states are **distinct and explicit** per the *never-fake-readiness* invariant — no collapsed Ready/NotReady boolean.

**Non-goals**
- G3 cloud-egress consent/visibility (panel exposes a status hook only — see §9).
- New model formats, new stages, or changes to `DefaultStageOrder`.
- Replacing the existing download/import dialogs — they are reused as the *provision* step's callbacks.

---

## 3. Core model — the selection-snapshot spine *(load-bearing)*

Everything hangs off the distinction between **draft** selections (mutable, edited in the UI) and a **frozen run snapshot** (immutable, the single source of truth for a run).

```
            ┌─ draft selections (UI) ──────────────────────────────┐
 edit tier  │   model tiers · diarization toggle · target lang ·    │
 ─────────► │   per-speaker voices                                  │
            └──────────────┬───────────────────────────────────────┘
                           │  (debounced)
                  Evaluate(draft) ──► PipelineReadinessReport ──► panel + per-stage badges
                           │
            user clicks Run│  FREEZE
                           ▼
                 RunReadinessSnapshot  (immutable; anchored on ExecutionSnapshot)
                           │
              Provision(snapshot) ──► download/import dialogs (batched, once)
                           │
                  Run(snapshot) ──► stage loop reads ONLY the snapshot
```

**Invariant:** `Evaluate`, `Provision`, and `Run` for a given run all read the **same** `RunReadinessSnapshot`. "Live re-validate on change" operates only on the **draft**; pressing Run freezes the draft into the snapshot. This is what structurally prevents defect #3 (and is anchored on the `ExecutionSnapshot` already captured in `TrackdubDubbingEngine`, extended to carry the full `RuntimeModelSelections`).

---

## 4. The Evaluate / Provision split

Today the throw-based per-stage `Ensure*` **conflates** "what's the status?" with "go make it ready." Split them:

### 4a. Read side — `IPipelineReadinessService` (Application/Contracts)
```
PipelineReadinessReport EvaluateAsync(
    IReadOnlyList<RuntimeStage> enabledStages,
    RuntimeModelSelections selections,           // draft (panel) or frozen (gate)
    TranscriptProjectState state,                // for resumable-stage detection
    CancellationToken ct)
```
- Pure read. For each enabled stage: call `IRuntimePlanner.PlanAsync` (already produces `StageRuntimePlan` with `Blocked` / `DownloadRequired` + `Fallback` + `ModelId`), plus a **cloud-key probe** for cloud aliases and a **consent probe** for voice-clone TTS.
- Skips stages with valid existing artifacts (`StageArtifactResumeEvaluator.CanResumeStage`) → reported `Satisfied (resumable)`, no model required.
- **Debounced + cached + per-stage invalidation** (it touches disk and EP probes). Cache key = `(stage, selection-for-stage, source-artifact fingerprint)`; a draft edit invalidates only the stages it affects.

### 4b. Mutate side — `RuntimeModelSetupCoordinator.EnsurePipelineModelsAvailableAsync`
```
RuntimeModelSetupResult EnsurePipelineModelsAvailableAsync(
    TranscriptWorkspace workspace,
    RunReadinessSnapshot snapshot,
    RuntimeModelSetupCallbacks callbacks,
    CancellationToken ct)
```
- Builds the batched request list from the report's not-ready stages and loops the **existing** `RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync` (Download / Import / Skip / Cancel) — one consolidated dialog pass instead of N scattered ones.
- Returns `RuntimeModelSetupResult(IsReady, SkippedStages)`; `SkippedStages` still honors optional **Separation** only.

### 4c. Per-stage `Ensure*` → non-interactive assert
The existing per-stage calls in the stage runners are **demoted, not deleted**: they become a cheap `EnsureModelsAvailableAsync` assert that **throws/logs** if a model is somehow absent (defense-in-depth; still honors *never-fake-readiness*) but **never prompts**. Prompting happens only in the consolidated gate.

---

## 5. Readiness states — *never-fake-readiness* mapping

The report's status type **is** the distinct-states enum the invariant demands, not a boolean. Each maps to a panel badge and a resolve action:

| CLAUDE.md distinct state | `ReadinessState` | Panel badge | Resolve action |
|---|---|---|---|
| provider registered | `ProviderMissing` | ⚠ "no provider" | (config/blocked) |
| runtime installed | `RuntimeMissing` | ⚠ "install runtime/EP" | EP install workflow |
| model files present | `DownloadRequired` / `ImportRequired` | ⬇ "download" / 📁 "import" | Download / Import (existing dialogs) |
| checksum verified | `IntegrityFailed` | ✖ "checksum mismatch" | re-download |
| license reviewed | `LicenseReviewRequired` | 📜 "review license" | `EpVendorLicenseDialog` |
| commercial mode allowed | `CommercialBlocked` | 🚫 "non-commercial blocked" | (blocked; switch model) |
| (cloud) key present | `CloudKeyMissing` | ☁⚠ "set API key" | API keys dialog → §9 hook |
| (consent) clone consent | `ConsentRequired` | 🔒 "consent needed" | `VoiceCloneConsentDialog` |
| ready | `Ready` | ✓ | — |
| stage will be skipped | `Satisfied` (resumable) / `SkippableOptional` (Separation) | ◌ "cached" / "optional" | — |

The panel renders one row per enabled stage with its state; the pre-run backstop refuses to start while any stage is in a blocking state (`ProviderMissing`, `RuntimeMissing`, `Download/ImportRequired`, `IntegrityFailed`, `LicenseReviewRequired`, `CommercialBlocked`, `CloudKeyMissing`, `ConsentRequired`) unless it is resolvable inline.

---

## 6. Components by layer (dependency flow preserved)

| Layer | Change |
|---|---|
| `Trackdub.Contracts` | `ReadinessState` enum; `StageReadiness` + `PipelineReadinessReport` records; `IPipelineReadinessService`; extend `RunReadinessSnapshot` (or reuse `ExecutionSnapshot`) to carry full `RuntimeModelSelections`. |
| `Trackdub.Application` | `PipelineReadinessService` (Evaluate via `IRuntimePlanner` + cloud-key + consent probes, with cache/invalidation); `EnsurePipelineModelsAvailableAsync` on `RuntimeModelSetupCoordinator`; demote per-stage `Ensure*` to assert. |
| `Trackdub.Composition` | Register `IPipelineReadinessService`; both app and SDK resolve the **same** registration. |
| `Trackdub.Sdk` | `TrackdubDubbingEngine.RunPreFlightChecksAsync` → call the shared service; **move provisioning fully up front** and **delete** the `stageProvisionedDuringExecution` mid-stage-download branch (see §8). |
| `Trackdub.App.Avalonia` | RunConfig **readiness panel** (VM binds to `PipelineReadinessReport`); **live revalidation** hook on selection-change; **pre-run backstop** in `RunPipelineStage`; per-stage `Ensure*` calls demoted. No inference/model code added here — VM binds to Contracts only. |

Layer-boundary check: inference/model logic stays in `Inference`/`Application`; the App VM binds to a Contracts DTO; no SQL in VMs; pipeline truth stays in `Application`/`Sdk`.

---

## 7. Live re-validation

- **Triggers:** changes to model-tier pickers, the diarization toggle, translation target language, and per-speaker voice assignment.
- **Mechanism:** each trigger updates the draft selections, **debounces** (~300 ms), then calls `EvaluateAsync` for **only the affected stages** (per-stage cache invalidation) and updates that stage's badge. The frozen snapshot, if any, is marked **stale** so the next Run re-freezes.
- **Non-blocking:** evaluation never opens a dialog; it only recomputes badges. Provisioning dialogs appear solely from the explicit panel "Resolve" button or the pre-run backstop.

---

## 8. Headless unification — extract and share

`unify` = both paths call **one** `IPipelineReadinessService`, and provisioning **moves fully up front** in headless:

- `RunPreFlightChecksAsync` calls `EvaluateAsync` for all stages → if anything is `DownloadRequired`/`ImportRequired` and `CanAutoDownload`, **provision up front** (auto-download) before the stage loop; otherwise return one **aggregated** `PreFlightFailed` listing every unmet stage.
- **Delete** the `stageProvisionedDuringExecution` branch so no model downloads mid-stage. 
- **Behavior change (call out for review):** headless runs now pay all download time *before* the first stage and **fail fast** with a single aggregated error instead of dribbling failures mid-run. This is the intended consistency win; confirm it's acceptable for SDK/CLI consumers.

---

## 9. Edge cases

- **Cloud aliases:** readiness = **API key present** (default; validity check is on-demand, never blocks the panel on a network call). Reported as `Ready` or `CloudKeyMissing`. *This is the single G3 hook — the panel row shows "Cloud (DeepL): key set ✓/✗". Do not extend into egress consent here.*
- **Voice-clone consent:** a TTS readiness requirement when any speaker has a reference clip — reported `ConsentRequired`, resolved by the existing `VoiceCloneConsentDialog`. Keep the dialog; just route it through the report.
- **Optional Separation:** `SkippableOptional`; the gate allows skipping it (matches `IsOptionalRuntimeStage`).
- **Resumable stages:** valid existing artifacts (`StageArtifactResumeEvaluator`) → `Satisfied`, no model required, no badge action.
- **Snapshot mismatch fix:** unify diarization on `CreateRuntimeSelections(snapshot)` for both provision and execute (closes defect #3).

---

## 10. Build sequence (phased, each independently testable)

1. **Contracts** — `ReadinessState`, `StageReadiness`, `PipelineReadinessReport`, `IPipelineReadinessService`, snapshot extension.
2. **Application** — `PipelineReadinessService.EvaluateAsync` (over `IRuntimePlanner`) + `EnsurePipelineModelsAvailableAsync` (batch over `RuntimeModelSetupWorkflow`); demote per-stage `Ensure*` to assert. Unit-tested in isolation with `TestDoubles`.
3. **Composition** — single registration; resolved by both hosts.
4. **SDK** — route `RunPreFlightChecksAsync` to the service; move provisioning up front; delete mid-stage branch. Tests for aggregated-fail + up-front provision.
5. **App** — readiness panel + live revalidation + pre-run backstop; demote per-stage calls.
6. **Cleanup** — fix the diarization selection-snapshot mismatch.

---

## 11. Tests

Cover, per the pipeline-change convention (success / skipped / missing-prereq / failure) plus G5-specifics:
- `Ready` end-to-end; `DownloadRequired` → provision → ready; `ImportRequired` → pick file → ready; `Blocked`/`CommercialBlocked` → backstop refuses.
- **Cancel** in the consolidated gate aborts the run cleanly (no partial stage).
- **Selection change invalidates + re-badges** only the affected stage; frozen snapshot marked stale.
- **Snapshot consistency:** the selections seen by `Evaluate`, `Provision`, and `Run` are identical (regression test for defect #3).
- Cloud alias **key present** vs **key missing**; optional **Separation skip**; **resumable** stage reported `Satisfied`.
- Headless **aggregated failure** lists all unmet stages; headless **up-front provision** downloads before the stage loop (no mid-stage download).

---

## 12. Risks / open questions

- **Evaluate cost** (disk + EP probes): mitigated by debounce + per-stage cache keyed on selection + source fingerprint. Risk: stale cache if an artifact changes underneath — invalidate on artifact-store writes.
- **SDK behavior change** (§8): longer pre-run, fail-fast. Needs explicit sign-off from CLI/API consumers.
- **Cloud key validity** is a network call — default to "present" semantics; only validate on explicit user action to avoid coupling the panel to network state.
- **Open:** does `IRuntimePlanner.PlanAsync` already surface `LicenseReviewRequired` / `CommercialBlocked`, or do those need to be lifted from the manifest/license catalog into the report? Confirm during Phase 2.
