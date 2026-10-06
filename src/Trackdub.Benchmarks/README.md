# src/Trackdub.Benchmarks

## Purpose

Benchmark harness.

## What belongs here

Console and in-app benchmark core.

## What should not go here

Production UI or project-specific artifacts.

## Stage-focused matrix

The `controlled-matrix` command runs the existing controlled benchmark once for
each selected pipeline stage and writes a matrix report containing each stage's
`BenchmarkEvidenceReport`:

```bash
dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- \
  controlled-matrix fixture.mp4 \
  --output benchmark-matrix \
  --stages vad,diarization,asr,translation,tts,export
```

With no `--stages`, the command uses the canonical extended stage catalog. Use
`--model stage=alias` to pin a model for an individual stage. The matrix keeps
stage-level evidence separate from the BDN microbenchmark artifacts.

## Structure & extension points

Three seams exist so telemetry lifetime, CLI parsing, and best-effort error handling
are not duplicated across call sites. Preserve them when extending the harness:

- **Peak monitoring** — `WorkingSetPeakMonitorFactory` is the only place that creates a
  `WorkingSetPeakMonitor`. `StageResourceTelemetryCapture`, `ControlledDubbingBenchmarkRunner`,
  and `SeparationEvalRunner` create their monitors through `IWorkingSetPeakMonitor` via the
  factory; the owner that starts a sampling window still calls `Stop()` at its boundary.
  Add an injected `IWorkingSetPeakMonitorFactory` rather than `new WorkingSetPeakMonitor(...)`.
- **CLI parsing** — the `controlled` and `controlled-matrix` commands share one option
  surface. `ControlledBenchmarkCliBinder` owns the shared flags, value reads, and options;
  each command keeps only its divergent arms (`--stage` vs `--stages`, `--model` shapes,
  `--report-dir`). Leaf bounds validation stays in `ResourceTelemetryOptionsParser`.
  Never re-open a bounds or shared-flag switch arm in an entry point.
- **Error routing** — `SeparationEvalRunner` funnels setup and per-job failures through
  separate file-load, setup, and per-job exception filters. The per-job boundary rethrows
  caller-requested cancellation; unrequested cancellation there becomes job failure.
  Extend the filter at the affected boundary, not a runner-wide catch.
- **Typed memory telemetry** — `BenchmarkEvidenceReport` (schema v2) carries the run-level
  process envelope in `ProcessMemory` and per-stage GC deltas in `StageGarbageCollection`.
  Per-stage working-set peaks and managed allocation already live in the typed
  `ResourceTelemetry` checks and `ResourceDistribution`. There is no string-keyed memory
  map: add a typed field rather than reintroducing key/value memory dictionaries.
- **Telemetry exception filters** — `TelemetryExceptionFilters` names the swallowed
  exception sets for continuous working-set sampling, stage-boundary collection, and the
  process snapshot probe. These sets differ on purpose: each boundary has its own
  supported OS and plugin failures, so add a named predicate per boundary
  rather than merging or widening an existing one.
  `tests/Trackdub.Benchmarks.Tests/Metrics/TelemetryExceptionFiltersTests` locks each
  accept/reject boundary.
- **Sampling dilation warnings** — `WorkingSetPeakMonitor` measures inter-tick gaps and
  warns past 4x cadence (`DescribeDilationWarning`); the warning annotates passing
  `workingSetBytes` checks, the run-level `workingSetPeakSampling` configuration, and
  each `SeparationEvalResult` (`peak_working_set_sampling_warning` in results JSONL).
  It is advisory by design — never fail a stage on it. Ticks come from the injected
  `ISamplingTicker` (production: `PeriodicTimer`); script tick gaps in tests to prove
  dilation handling deterministically. Wall-clock timing assertions in tests must assert
  ordering against a scaled outlier (see the p50 multi-run test), not an absolute
  millisecond budget that loaded nodes can cross.

## Agent guidance

Keep changes scoped to this directory's purpose. If a task requires crossing boundaries, update the relevant architecture note or ADR first. Add XML docs to any new shared seam, and cover it with tests that lock the existing behavior.
