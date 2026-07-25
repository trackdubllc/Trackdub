# Implementation Plan — G4: Run Progress & ETA

**Source:** [design-g4-run-progress-eta.md](design-g4-run-progress-eta.md)

---

## Phase 1: Contracts — Progress model (1 day)

Add per-stage progress record.

**Files:**
- src/Trackdub.Contracts/Pipeline/StageProgressReport.cs

**Logic:**
- StageProgressReport: StageName, PercentComplete?, ItemsComplete, TotalItems?, EstimatedTimeRemaining?, DisplayLabel
- Immutable record

---

## Phase 2: Application — ETA + Context (2 days)

Throughput tracker + extend context.

**Files:**
- src/Trackdub.Application/Pipeline/StageThroughputTracker.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/TranscriptGenerationContext.cs

**Logic:**
- StageThroughputTracker: Report(itemsComplete, totalItems) → TimeSpan? ETA
  - Simple ms/item avg; suppress before 200ms elapsed
- TranscriptGenerationContext: add IProgress<StageProgressReport>? StageProgress field (optional, backward-compatible)

---

## Phase 3: Application — Stage progress wiring (3 days)

Add per-segment/region progress to handlers.

**Files:**
- (extend) src/Trackdub.Application/Transcripts/StartTtsStageHandler.cs
- (extend) src/Trackdub.Application/Transcripts/TranslationOrchestrationService.cs
- (extend) src/Trackdub.Application/Transcripts/Stages/AsrGenerationStage.cs

**Logic:**
- TTS handler: loop over segments, report per segment
- Translation service: loop over segments, report per segment
- ASR handler: loop over regions, report per region (use StageThroughputTracker)
- All use StageNames constant for StageName field

---

## Phase 4: SDK — Progress bridging (2 days)

Connect stage progress to pipeline events.

**Files:**
- (extend) src/Trackdub.Sdk/TrackdubDubbingEngine.cs

**Logic:**
- StageProgressAdapter: convert StageProgressReport → PipelineProgressEvent(kind=Progress)
- Thread IProgress<StageProgressReport> into TranscriptGenerationContext
- Download bridge (temporary until G5 lands): wrap ModelDownloadProgress → PipelineProgressEvent(Progress)
- Black-box stages (VAD/Diar/Sep): emit Progress event + optional periodic heartbeat

---

## Phase 5: App — VM progress fields (2 days)

Add progress bindings to view models.

**Files:**
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineStageRowViewModel.cs
- (extend) src/Trackdub.App.Avalonia/ViewModels/PipelineRunViewModel.cs

**Logic:**
- PipelineStageRowViewModel: ProgressPercent, IsIndeterminate, EtaText fields
- PipelineRunViewModel: StagesComplete, StagesTotal, OverallElapsedText, OverallEtaText fields
- Subscribe to PipelineProgressEvent stream, update per-stage + overall on Progress kind

---

## Phase 6: App — AXAML bindings (2 days)

Add progress bars + ETA display to UI.

**Files:**
- (extend) src/Trackdub.App.Avalonia/Views/PipelineStagesView.axaml
- (extend) src/Trackdub.App.Avalonia/Views/RunConfigView.axaml

**Logic:**
- ProgressBar binds to PipelineStageRowViewModel.ProgressPercent
- IsIndeterminate binds to IsIndeterminate (for VAD/Diar/Sep)
- EtaText label shows "~23s remaining" or "00:01:42 elapsed"

---

## Phase 7: CLI — Progress rendering (1 day)

Update CliProgressReporter for Progress kind.

**Files:**
- (extend) src/Trackdub.Cli/CliProgressReporter.cs

**Logic:**
- Handle PipelineProgressEventKind.Progress
- Render as: [Stage   ] ████░░░░░░ 45%  12 / 26 segments  (~2m 15s)
- Black-box stages show "running…" with elapsed

---

## Tests

- StageThroughputTracker returns null before 200ms; finite ETA after
- StageProgressAdapter maps StageProgressReport correctly
- Translation emits N events for N segments
- TTS emits N events for N speakers' segments
- ASR emits M events for M regions
- VAD/Diar/Sep emit at least one Progress event (PercentComplete=null)
- PipelineRunViewModel.StagesComplete increments on Completed/Skipped
