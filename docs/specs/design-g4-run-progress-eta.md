# Design Spec — G4: Run Progress & ETA

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G4** — Run = one click then long blind wait; per-stage progress exists (binary 0%/100%) but no intermediate progress; no ETA; first-run model downloads hide inside stages.

**Relationship to G5:** G5 moves model downloads fully up front (pre-run). G4 specifies what the user sees *during* the stage loop. After G5 lands, Phase 4 is clean inference time → G4's ETA is more accurate. G4 should still gracefully surface download progress in the interim (the `ModelDownloadProgress` infrastructure already has ETR).

**Scope:** progress emission, ETA computation, and surfacing in VM/CLI. No changes to stage execution logic or pipeline sequencing.

---

## 1. Problem — what exists vs. what fires

**Infrastructure present, not wired:**

| Component | Exists | Fires |
|---|---|---|
| `PipelineProgressEventKind.Progress` (kind=1) | ✅ | ❌ never emitted |
| `PipelineProgressEvent.Percentage` (0–100) | ✅ | ❌ always 0 or 100 |
| `PipelineProgressEvent.ElapsedDuration` | ✅ | ✅ at Completed/Failed |
| `ModelDownloadProgress.EstimatedTimeRemaining` | ✅ | ✅ in download dialog only |
| `PipelineStageRowViewModel.IsRunning` | ✅ | ✅ (boolean flip) |
| `PipelineStageRowViewModel.RunProgressText` | ✅ | ❌ never set during run |

**Current behaviour (verbatim from `PipelineRunViewModel`):**
```csharp
_activeStageDisplay = events
    .Select(e => $"{e.StageName}: {e.Percentage:P0}")  // "asr: 0%" entire ASR run
    .ToProperty(...)
```

**Result:** user sees `asr: 0%` for the full duration of ASR (which can be several minutes on first run), with no signal that progress is being made inside the stage.

**Two distinct wait sources:**
1. **Model download/Olive** — G5 moves this up front. Until G5 lands: can run for 1–15 min inside a stage with no progress shown.
2. **ONNX inference** — proportional to audio/segment count. Translation/TTS loop over N segments. ASR processes M VAD regions. VAD/Diarization/Separation are single-pass black boxes.

---

## 2. Goals / Non-goals

**Goals**
- Every in-flight stage shows meaningful intermediate progress (not 0%).
- Segmented stages (Translation, TTS, ASR-by-region) show `N/M segments` + throughput-based ETA.
- Non-segmented stages (VAD, Diarization, Separation) show elapsed time + activity pulse.
- Pipeline-level view shows overall `N/7 stages` + current stage detail.
- Headless/SDK path receives the same `Progress` events for CLI rendering.
- Download progress (when G5 not yet done) is surfaced at the pipeline level, not hidden.

**Non-goals**
- Wall-clock accuracy guarantees — ETA is best-effort throughput projection.
- GPU utilization meter (`GpuUtilization` already a stub in `PipelineRunViewModel` — not addressed here).
- Changing stage execution order or parallelism.

---

## 3. `StageProgressReport` — new progress unit

Add to `Trackdub.Contracts` (or `Trackdub.Sdk`):

```csharp
/// <summary>
/// Intermediate progress report emitted within a single pipeline stage.
/// </summary>
public sealed record StageProgressReport(
    string StageName,

    /// <summary>0–100 percentage. Null for activity-only stages (VAD, Diarization, Separation).</summary>
    double? PercentComplete,

    /// <summary>Items processed so far (segments, regions, chunks).</summary>
    int ItemsComplete,

    /// <summary>Total items. Null when total is unknown up front.</summary>
    int? TotalItems,

    /// <summary>Best-effort remaining time. Null when insufficient history.</summary>
    TimeSpan? EstimatedTimeRemaining,

    /// <summary>Human-readable label for display. E.g. "12 / 38 segments".</summary>
    string? DisplayLabel);
```

---

## 4. Threading progress into stages

### 4a. Extend `TranscriptGenerationContext`

`TranscriptGenerationContext` is an immutable record — add an optional progress reporter:

```csharp
public sealed record TranscriptGenerationContext(
    // ... existing fields ...
    IProgress<StageProgressReport>? StageProgress = null);
```

This is backward-compatible (default null); no change to `ITranscriptGenerationStage` interface.

Stages that can report progress call:
```csharp
context.StageProgress?.Report(new StageProgressReport(
    StageName: StageNames.Translation,
    PercentComplete: 100.0 * done / total,
    ItemsComplete: done,
    TotalItems: total,
    EstimatedTimeRemaining: ComputeEta(done, total, elapsed),
    DisplayLabel: $"{done} / {total} segments"));
```

### 4b. Extend `AsrStageHandler` / `AsrStageRequest`

ASR processes speech regions. Add `IProgress<StageProgressReport>?` to `AsrStageRequest` (or as a handler-level parameter). The handler reports per-region (or per-chunk if batched):

```csharp
// AsrStageHandler.HandleAsync inner loop:
for (int i = 0; i < regions.Count; i++)
{
    // ... transcribe region i ...
    progress?.Report(new StageProgressReport(
        StageNames.Asr,
        PercentComplete: 100.0 * (i + 1) / regions.Count,
        ItemsComplete: i + 1,
        TotalItems: regions.Count,
        EstimatedTimeRemaining: eta.Compute(i + 1, regions.Count),
        DisplayLabel: $"region {i + 1} / {regions.Count}"));
}
```

### 4c. Extend `StartTtsStageHandler` / `TranslationOrchestrationService`

Both already loop over segments — add progress report per iteration. Same pattern as ASR above.

### 4d. Non-segmented stages (VAD, Diarization, Separation)

These are single-pass black boxes with no natural checkpoints:
- Report `StageProgressReport(PercentComplete: null, ItemsComplete: 0, TotalItems: null, DisplayLabel: "running…")` at start.
- Report again with elapsed label at configurable heartbeat intervals (e.g. `PeriodicTimer` at 1s intervals from the calling layer, not from inside the engine itself — keep engine code clean).

---

## 5. ETA computation — `StageThroughputTracker`

Utility in `Trackdub.Application` (no inference dependencies):

```csharp
public sealed class StageThroughputTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _lastComplete;

    /// <summary>Call after each item completes. Returns best-effort ETA or null.</summary>
    public TimeSpan? Report(int itemsComplete, int totalItems)
    {
        if (itemsComplete <= 0 || totalItems <= itemsComplete)
            return null;

        double elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
        if (elapsedMs < 200) return null;  // too early to project

        double msPerItem = elapsedMs / itemsComplete;
        int remaining = totalItems - itemsComplete;
        return TimeSpan.FromMilliseconds(msPerItem * remaining);
    }
}
```

Simple throughput average. Good enough for segments (typically 10–200). For first-run with no prior data, shows null until 200ms elapsed — then projects.

---

## 6. Pipeline-level aggregation

`TranscriptGenerationPipeline.ExecuteAsync` (and `TrackdubDubbingEngine.ExecuteAsync`) already receive `IProgress<PipelineProgressEvent>`. They need to:

1. Create a `StageProgressAdapter` that converts `StageProgressReport → PipelineProgressEvent(kind=Progress)`:

```csharp
IProgress<StageProgressReport> stageProgress = new Progress<StageProgressReport>(report =>
{
    progress?.Report(new PipelineProgressEvent(
        StageName: report.StageName,
        EventKind: PipelineProgressEventKind.Progress,
        Percentage: report.PercentComplete ?? 0,
        Message: report.DisplayLabel,
        ElapsedDuration: TimeSpan.Zero)  // ETA in Message for now
    );
});
```

2. Thread `stageProgress` into the context:
```csharp
context = context with { StageProgress = stageProgress };
```

3. **Download progress bridge** (until G5 lands): `RuntimeModelSetupWorkflow`'s `callbacks.CreateDownloadProgress` already returns `IProgress<ModelDownloadProgress>`. Bridge it:
```csharp
IProgress<ModelDownloadProgress> downloadProgress = callbacks.CreateDownloadProgress(stageName);
// Wrap to also fire PipelineProgressEvent:
IProgress<ModelDownloadProgress> bridged = new Progress<ModelDownloadProgress>(p =>
{
    downloadProgress.Report(p);
    progress?.Report(new PipelineProgressEvent(
        StageName: stageName,
        EventKind: PipelineProgressEventKind.Progress,
        Percentage: p.PercentComplete,
        Message: $"Downloading model: {p.PercentComplete}%{(p.EstimatedTimeRemaining.HasValue ? $" (~{FormatEta(p.EstimatedTimeRemaining.Value)} remaining)" : "")}",
        ElapsedDuration: TimeSpan.Zero));
});
```

---

## 7. VM changes

### 7a. `PipelineStageRowViewModel` — add progress fields

```csharp
[ObservableProperty]
private double progressPercent;   // 0–100; binds to ProgressBar.Value

[ObservableProperty]
private bool isIndeterminate;     // true for VAD/Diar/Sep while running

[ObservableProperty]
private string? etaText;          // "~23s remaining" | "00:01:42 elapsed" | null
```

No existing fields removed — `RunProgressText` becomes the composite label ("12 / 38 segments").

### 7b. `PipelineRunViewModel` — overall view

Add:
```csharp
[ObservableProperty]
private int stagesComplete;       // M of N done

[ObservableProperty]
private int stagesTotal;          // N (enabled stages this run)

[ObservableProperty]
private string overallElapsedText; // "00:02:14"

[ObservableProperty]
private string? overallEtaText;   // "~4 min remaining" (sum of per-stage ETAs)
```

`PipelineRunViewModel` subscribes to progress events and:
- On `Started`: increment `stagesTotal` (or pre-compute from enabled stages), mark stage `IsRunning=true`.
- On `Progress`: update `ProgressPercent`, `EtaText`, `RunProgressText` on the matching `PipelineStageRowViewModel`.
- On `Completed`/`Skipped`/`Failed`: flip `IsRunning=false`, `ProgressPercent=100` (or 0 for skipped).
- Every 1s: update `overallElapsedText` from a `DispatcherTimer` or Rx interval.

### 7c. Overall ETA

Aggregate per-stage ETAs when available. When a stage has no ETA (black-box stages), show `null` contribution. Display `overallEtaText` only if ≥1 stage provides an ETA:

```
~4 min remaining  (Stage 3/7: Translation — 18 / 52 segments)
```

If no stage-level ETA: show elapsed only — `"00:02:14 elapsed"`.

---

## 8. CLI / headless progress rendering

`CliProgressReporter` already implements `IProgress<PipelineProgressEvent>`. Extend to handle `kind=Progress`:

```
[ASR      ] ████████░░░░░░░░░░░░ 40%  region 8 / 20  (~1m 23s remaining)
[Translate] ████████████████████ 100% completed in 00:00:42
[TTS      ] ████░░░░░░░░░░░░░░░░ 22%  segment 11 / 50  (~3m 10s remaining)
```

---

## 9. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Contracts` / `Trackdub.Sdk` | `StageProgressReport` record; no breaking changes to existing types |
| `Trackdub.Application` | `StageThroughputTracker`; extend `TranscriptGenerationContext` with `IProgress<StageProgressReport>?`; per-segment progress in `AsrStageHandler`, `StartTtsStageHandler`, `TranslationOrchestrationService` |
| `Trackdub.Sdk` | `StageProgressAdapter` (report bridge); `PeriodicHeartbeat` for black-box stages; download bridge; `ReportProgress` fires `kind=Progress` |
| `Trackdub.App.Avalonia` | `PipelineStageRowViewModel.ProgressPercent` + `IsIndeterminate` + `EtaText`; `PipelineRunViewModel` overall view; AXAML binds `ProgressBar` to new fields |
| `Trackdub.Cli` | `CliProgressReporter` handles `kind=Progress` |

Layer boundaries: no inference code in VM; `StageThroughputTracker` in Application with no I/O; progress events remain in Sdk/Contracts.

---

## 10. Build sequence

1. **Contracts** — `StageProgressReport`. No other changes yet.
2. **Application** — `StageThroughputTracker`; extend `TranscriptGenerationContext`; add per-segment reporting to `TranslationOrchestrationService` and `StartTtsStageHandler` first (lowest risk, highest visibility — TTS is often the longest stage).
3. **Application** — `AsrStageHandler` per-region progress.
4. **Sdk** — `StageProgressAdapter` + heartbeat for VAD/Diar/Sep + download bridge + `ReportProgress` fires `Progress` kind.
5. **App** — VM fields + AXAML progress bars. `PipelineRunViewModel` subscription updates.
6. **CLI** — `CliProgressReporter` handles `Progress` kind.

TTS + Translation (step 2) deliver the most visible improvement first. Heartbeat stages (step 4) are pure wrapping — low risk.

---

## 11. Tests

- `StageThroughputTracker.Report` returns null before 200ms; returns finite ETA after; clamps on `itemsComplete >= totalItems`.
- `StageProgressAdapter` maps `StageProgressReport → PipelineProgressEvent(kind=Progress, Percentage=report.PercentComplete)`.
- Translation handler emits N `StageProgressReport` events for N segments (fake `ITranslationEngine`).
- TTS handler emits N events for N speakers' segments.
- ASR handler emits M events for M regions.
- Black-box stages (VAD/Diar/Sep) emit at least one `Progress` event with `PercentComplete=null`.
- Download bridge emits `kind=Progress` with `Percentage = ModelDownloadProgress.PercentComplete`.
- `PipelineRunViewModel`: `stagesComplete` increments on `Completed`/`Skipped`; stage `ProgressPercent` updates on `Progress`; `IsRunning` flips correctly.

---

## 12. Risks / open questions

- **ASR region count:** VAD output is `IReadOnlyList<SpeechRegion>` — count known before ASR starts. Good. But if a cloud ASR engine batches all regions into one HTTP call, per-region progress isn't available — show download-style bytes-received if possible, else indeterminate.
- **Translation batching:** `ITranslationEngine.TranslateAsync` takes a full `TranslationRequest` (all segments at once) — cloud engines (DeepL/OpenAI) are one HTTP call per batch. For cloud, progress is either before/after (binary) or via streaming response parsing. Spec the interface for per-segment progress but accept indeterminate fallback for cloud MT (cloud translation is fast; this is low priority).
- **Thread safety:** `IProgress<T>` callbacks fire on whichever thread calls `Report`. `TranscriptGenerationPipeline` runs on a task thread; `DispatcherTimer` for overall elapsed runs on UI thread. Ensure the Rx `ObserveOn(RxApp.MainThreadScheduler)` in `PipelineRunViewModel` marshals all updates — already present, just verify it covers new fields.
- **G5 ordering:** Download bridge (§6) is a short-lived shim until G5 front-loads provisioning. Mark with `// TODO(G5): remove download bridge once G5 Phase 4 (SDK) lands`.
