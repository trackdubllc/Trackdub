# End-to-End Pipeline Trace

[← Back to index](README.md)

The only pipeline with a complete producer→consumer chain is the **local dubbing run**. Both initiators (desktop per-stage button, CLI `run-pipeline`) converge on `DubbingPipelineEngine.ExecuteAsync` (`Trackdub/src/Trackdub.Application/Dubbing/DubbingPipelineEngine.cs:58-187`).

## Stage: initiation

- **Expected:** user action or CLI flag produces validated run options. **Actual:** yes. Desktop builds `DubbingSessionOptions` off-thread with UI-thread marshaling for `SpeakerCards`/model prefs (`AvaloniaMainWindowViewModel.PipelineStageExecutionHost.cs:97-243`); CLI maps its request record 1:1 (`RunPipelineHandler.cs:~40-64`).
- **State ownership:** shell owns rows/dialogs/run-group UX; engine owns semantics. Clean.
- **Failure:** picker cancel / declined clone consent / unrunnable stage ⇒ host returns `null`, executor returns `null` without inventing a result (`PipelineStageExecutor.cs:38-49`). Honest. **Observability:** status-bar string only. No correlation id spans shell↔engine↔files.

## Stage: preflight & runtime selection

- **Expected:** refuse runs whose models/hardware are absent. **Actual:** `RunPreFlightChecksAsync` then re-resolve selections and merge into the snapshot (`:~100-160`). Failures surface as `PreFlightFailed` + `PreFlightFailures[]`, and both describers print them (`PipelineRunResultDescriber.cs`).
- **Risk:** plan/cache coherence (H-3). `CompositeModelCacheInventory` concatenates local + manifest-derived records (`:21-31`, `CompositionRoot.cs:522-527`), so an inventory-sourced identity can outrank a corrupt local record; `RuntimePlannerCacheIndexBuilder` caches for 30s and is invalidated only via `RuntimePlanner.cs:238`.
- **Evidence:** tests assert hash-mismatch → DownloadRequired (`RuntimePlannerTests.cs:891-913`) and skip-download-with-empty-sha (`ModelDownloadOrchestratorTests.cs:546-572`) — each side green, the joint transition never tested.

## Stage: separation → cleanup → VAD/ASR → diarization → overlap rescue → refinement

- **Expected:** prerequisite failures stop downstream; transient failures retry; skips log exact reasons. **Actual:** the loop runs each stage, skips with `"CANCELLED"` on cancellation (`:341-416`), and marks `failedPrerequisiteStage` only when a stage in `PrerequisiteStages` **Failed** (`:~370`). `DubbingPipelineStages.PrerequisiteStages` = `{Vad, Asr, Translation, Tts}` — **Separation and Diarization are not prerequisites**, so a failed separation lets ASR proceed on the un-separated spine.
- `ExecuteStageAsync:932-1044` converts every exception into a `Failed` outcome and **never propagates**; the transient classifier tags `STAGE_FAILED_TRANSIENT` with an inline comment conceding "The engine itself has no retry loop." No async job queue, no retries, no idempotency key anywhere; resumption is by `HasValidExistingArtifactsAsync` + `ForceRerun`.
- **Observability:** degradation records + stage-run rows; no metrics, no retry counter to read.

## Stage: translation → TTS → lipsync/lip-synthesis

- **Expected:** language pair validated, cloning consented. **Actual:** `RequiresTargetLanguage` = Translation, Tts; cloning consent enforced on the desktop builder before options are returned (`:132-149`). Tier-free watermark path unaffected.
- **Model/auth handoff:** `CloudAware*Engine` alias routing sends audio/text to Google/Gemini/ElevenLabs using separately configured credentials — a second, product-adjacent cloud boundary with no relationship to `api.trackdub`. Gemini model IDs pinned at `gemini-1.5-flash` / `gemini-1.5-pro` (`GeminiCloudTranslationEngine.cs:18`, `GeminiCloudTranscriptionEngine.cs:21`).

## Stage: export

- **Expected:** tier cap and watermark applied; atomic delivery. **Actual:** cap checked before any encoding with null-guard (`ExportStageHandler.cs:81-88`), watermark resolved at `:401` and consumed by `FfmpegMuxer.cs:84,187,199`; stale `.bak` sidecars swept at stage start (`:98-101`); delivery uses a rollback path list with `exportSucceeded`-gated `finally` cleanup. This is the best-hardened stage in the repo.
- **Gate caveat:** `DesktopExportTierGate.EnsureInitialized` is sync-over-async (`GetAwaiter().GetResult()`), and leaves `initialized = false` if initialization throws, so every subsequent export re-attempts blocking init. `App.axaml.cs:295` warms `RequiresWatermark` at startup, which usually absorbs that cost.

## Stage: reporting (where the system diverges)

- **Desktop:** `DescribeRunResult` / `DescribeTerminalOutcome` preserve Failed / Skipped / PartiallySucceeded / cancelled distinctly and return `null` for plain success so the row falls back to persisted-run refresh. **No status laundering.**
- **CLI/automation:** the same `DubbingRunResult` reaches `DetermineOverallStatus:2505-2550`, where *any failed + any succeeded/partial* ⇒ `PartialSuccess`, and `RunPipelineHandler.cs:65-83` maps `PartialSuccess` → **exit 0**. Downstream stages of a failed prerequisite become benign `PREREQUISITE_FAILED` skips (`StageSkipReasonCodes.cs:8-37`), so the failure summary loop at `:96-115` prints a `StageFailed` line to stderr while the process still exits 0. Any script, CI job, or SDK caller keying on exit code records a successful dub for a project with no output.
