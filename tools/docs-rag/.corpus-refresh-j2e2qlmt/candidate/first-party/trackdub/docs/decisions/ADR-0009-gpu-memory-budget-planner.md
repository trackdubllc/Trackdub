# ADR-0009: GPU memory budget planner

- Status: Draft
- Date: 2026-05-10

## Context

Trackdub loads multiple ONNX models into GPU memory during a session — at minimum a VAD model, an ASR
model, a diarization model, and a TTS model. Each model wrapper (`IInferenceEngineAdapter`) allocates GPU
resources when constructed and frees them when disposed.

Currently there is no coordination between model allocations. If the combined model working set exceeds
available GPU memory, one of three things happens:

1. DirectML falls back to CPU for one or more models, silently degrading performance.
2. The GPU driver triggers a TDR (timeout-detection-recovery) that stalls the pipeline for seconds.
3. An allocation fails with an `OutOfMemoryException` that propagates as a hard pipeline failure.

None of these outcomes is surfaced to the user as a memory-planning decision. The user cannot tell whether
their GPU can run the selected model combination, or which model would be swapped to CPU if memory runs low.

## Decision

Introduce a **GPU memory budget planner** — a service that estimates the GPU memory footprint of a set of
models before any of them are loaded, and either confirms the plan fits or recommends a degradation strategy.

### Components

**`GpuMemoryBudgetEstimator`** — reads model manifest entries (`ModelManifestEntry`) to obtain per-model
VRAM requirements. Each manifest entry gains an optional `EstimatedVramMb` field (integer, nullable).
If a model has no estimate, the planner uses a worst-case default (e.g. 2 GB for ASR, 512 MB for VAD).

**`IGpuMemoryBudgetPlanner`** — the public interface.

```
bool TryPlan(
    IReadOnlyList<ModelManifestEntry> models,
    long availableVramBytes,
    out GpuMemoryPlan? plan);
```

**`GpuMemoryPlan`** — describes which models fit on GPU, which must run on CPU, and whether the plan
is degraded.

```
public sealed record GpuMemoryPlan(
    bool IsDegraded,
    IReadOnlyList<ModelMemoryAssignment> Assignments);

public sealed record ModelMemoryAssignment(
    string ModelId,
    long EstimatedBytes,
    ExecutionProvider AssignedProvider);
```

**`IVramQuery`** — a small abstraction over DXGI / CUDA API calls that returns the current available
dedicated GPU memory in bytes.

```
public interface IVramQuery
{
    long GetAvailableDedicatedBytes();
}
```

### Integration points

1. `ModelManifestEntry` gains `EstimatedVramMb` (optional, nullable).
2. `GpuMemoryBudgetEstimator` reads manifest entries and computes assignments.
3. The budget planner is called **once per session** during `SessionWorkflowCoordinator` initialization,
   before any engine adapter is constructed.
4. The resulting `GpuMemoryPlan` is passed as part of the pipeline's immutable execution snapshot so stages
   can select the correct `IInferenceEngineAdapter` (GPU or CPU) per model.
5. If `IVramQuery` cannot determine available memory (unsupported API, driver issue), the planner assumes
   a conservative default (e.g. 2 GB) and logs a warning.

### Surface

- A degraded plan is logged and exposed through `PipelineDegradationRecord` (one record per model that was
  bumped to CPU).
- A dedicated "GPU Memory" section in the diagnostic overlay shows the plan, per-model assignment, and
  total estimated vs available.

## Consequences

Positive:

- The user gets a predictable model-loading experience — no surprise mid-pipeline fallback to CPU.
- Degradation is recorded in the pipeline audit trail, making it diagnosable post-hoc.
- The planner decouples VRAM measurement from model loading, keeping engine adapters simple.

Negative:

- `EstimatedVramMb` in the manifest is a static estimate; actual VRAM usage varies by input size and
  runtime state. The estimate should be conservative (overestimate by ~20%).
- `IVramQuery` depends on OS-level GPU APIs that may not be available on all Windows configurations or
  may report inaccurate values under dynamic load.
- The planner adds a new service interface and implementation that must be maintained alongside the
  inference engine adapters.

## Alternatives considered

### Dynamic VRAM tracking (measure actual usage at runtime)

Rejected because:
- DXGI `QueryVideoMemoryInfo` reports total budget / current usage, not per-process usage.
- CUDA `cudaMemGetInfo` reports free memory but race-conditions with concurrent model loads make it
  unreliable for planning.
- Actual measurements come too late — by the time we know memory is tight, models are already loaded.

### No planner — rely on DirectML CPU fallback

Rejected because silent CPU fallback is the current broken behavior. The user needs visibility into
when and why fallback occurs.

## References

- [DXGI.QueryVideoMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)
- [ONNX Runtime memory-optimization guidance](https://onnxruntime.ai/docs/performance/model-optimizations/memory.html)
- Existing `ModelManifestEntry` — `src/Trackdub.Inference/Runtime/ModelManifest/`
