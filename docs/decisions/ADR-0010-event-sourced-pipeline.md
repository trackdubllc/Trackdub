# ADR-0010: Event-sourced pipeline

- Status: Draft
- Date: 2026-05-10

## Context

The current transcript-generation pipeline (TranscriptPipelineBuilder) runs stages sequentially. Each stage
receives a PipelineContext, mutates it, and passes it to the next stage. The pipeline result is the final
state of PipelineContext after all stages complete.

This mutable-passing design has several drawbacks:

1. Mid-run corruption - If a UI action or provider change mutates shared state while a stage is running,
   the pipeline context can end up in an inconsistent state. The recent architectural decision to prefer
   immutable execution snapshots tried to mitigate this at the session level, but the per-stage context
   remains mutable.

2. Lost intermediate state - There is no record of what each stage produced, only the final aggregated
   result. Debugging a failed stage requires re-running the entire pipeline.

3. Audit gap - PipelineDegradationRecord captures skip/failure reasons at specific points, but there is
   no ordered event log that answers "what happened during this run, stage by stage?"

4. Replay impossibility - Because intermediate state is overwritten, the pipeline cannot resume from a
   specific stage after a crash or cancellation.

## Decision

Replace the mutable PipelineContext pass-through with an event-sourced pipeline that appends immutable
stage-completion events to an ordered event log.

### Architecture

TranscriptRunEvent - a discriminated union of all possible stage outcomes:

```
public abstract record TranscriptRunEvent;
public sealed record StageStarted(string StageName, Instant Timestamp) : TranscriptRunEvent;
public sealed record StageCompleted(
    string StageName, StageResult Result, Instant Timestamp) : TranscriptRunEvent;
public sealed record StageSkipped(
    string StageName, string Reason, Instant Timestamp) : TranscriptRunEvent;
public sealed record StageFailed(
    string StageName, PipelineDegradationRecord Degradation, Instant Timestamp) : TranscriptRunEvent;
```

TranscriptRunJournal - in-memory ordered collection of TranscriptRunEvent items:

- Appends are thread-safe (lock around the list).
- The journal is part of the immutable execution snapshot so it is not visible to stages until after they
  complete.
- The journal is serializable to JSON for crash recovery and diagnostic export.

Pipeline execution change:

- Each stage reads its input from the immutable execution snapshot (not from a mutable PipelineContext).
- Each stage writes its output as a TranscriptRunEvent appended to the journal.
- The final pipeline result is derived by folding the journal: fold over StageCompleted events to produce
  the aggregate result (previously done via PipelineContext mutation).
- PipelineContext is removed entirely; its responsibilities are absorbed by the execution snapshot
  (input) and the journal fold (output).

### Migration

1. Add TranscriptRunEvent types to Trackdub.Domain.
2. Add TranscriptRunJournal to Trackdub.Application transcript pipeline.
3. Replace PipelineContext references in TranscriptPipelineBuilder with journal + snapshot.
4. Migrate each stage handler one at a time: the handler reads from snapshot fields instead of
   PipelineContext, and the caller appends a StageCompleted event.
5. Remove PipelineContext after all handlers are migrated.

## Consequences

Positive:

- Full audit trail: every stage outcome is recorded in order, including skips and degradations.
- Crash recovery: the journal can be persisted and used to resume from the last completed stage.
- Thread safety: because the journal is append-only and stages cannot see uncommitted events, there is
  no risk of mid-run context corruption.
- Debugging: a developer can inspect the journal to see exactly what happened in a run.

Negative:

- More allocation per stage: each stage completion allocates a record object instead of mutating in place.
  This is acceptable because the pipeline runs at most once per user action.
- Existing stage handlers reference PipelineContext; the migration requires touching every handler.
- The event types introduce a new abstraction layer (events) on top of the existing StageResult types.

## Alternatives considered

### Keep mutable PipelineContext, add deep-copy snapshots

Rejected because deep-copy snapshots are fragile and expensive for large context objects. The event-sourced
approach is more idiomatic for .NET and provides a clearer audit trail.

### Use System.Reactive (IObservable) for event streaming

Rejected because Reactive Extensions add a significant dependency for what is fundamentally a simple
append-only list. The journal pattern is explicit, testable, and has zero external dependencies.

## References

- Event sourcing pattern (Martin Fowler) - https://martinfowler.com/eaaDev/EventSourcing.html
- Existing TranscriptPipelineBuilder - src/Trackdub.Application/Transcripts/Pipeline/TranscriptPipelineBuilder.cs
- Existing ITranscriptGenerationStage - src/Trackdub.Application/Transcripts/Pipeline/ITranscriptGenerationStage.cs
