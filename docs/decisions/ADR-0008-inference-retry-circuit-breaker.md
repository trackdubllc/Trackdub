# ADR-0008: Inference Retry / Circuit Breaker

- Status: Draft
- Date: 2026-05-10

## Context

Trackdub runs ONNX inference through a routing layer (`RoutedTtsEngine`, `RoutedAudioTranscriptionEngine`, etc.) that selects a concrete engine adapter (Kokoro, Whisper, Demucs, etc.) based on a `StageRuntimePlan`. Each adapter calls into `InferenceSession` for model execution.

Currently, **no retry or circuit-breaker logic exists** anywhere in the inference path:

1. **Transient failures** (GPU kernel timeout, driver TDR, `OrtException` with ephemeral causes) propagate directly to the pipeline caller, failing the entire stage.
2. **Model-load failures** (corrupt file, EP mismatch, OOM during `InferenceSession` construction) are not distinguished from runtime failures, so repeated pipeline attempts retry a doomed load.
3. **No backpressure mechanism** ΓÇö a failing EP (e.g., TensorRT-RTX with an unsupported opset) is retried immediately on every pipeline run without cooling off.
4. **`PipelineDegradationRecord`** is written for general degradation but has no structured inference-fault taxonomy or circuit state.

This is a reliability gap. A single transient GPU glitch or one corrupt model download should not require a process restart or manual intervention.

## Decision

Introduce a two-layer inference reliability system:

### Layer 1 ΓÇö Retry Policy (transient failures)

Wrap each engine adapter's `RunAsync` (or equivalent Tensor-based inference call) in a retry handler that catches known transient ONNX exceptions:

| Exception / Signal | Classification | Action |
|---|---|---|
| `OrtException` with `OrtErrorCode.OrtErrorCode.RUNTIME_EXCEPTION` | Transient | Retry up to 2├ù with exponential backoff (100ms base, 2├ù factor, capped at 2 s) |
| `OrtException` with `OrtErrorCode.OrtErrorCode.ENGINE_ERROR` | Possibly transient | Retry once after 500 ms |
| `OrtException` with `OrtErrorCode.OrtErrorCode.MODEL_LOAD` | Permanent (model) | Route to circuit breaker |
| `OutOfMemoryException` / GPU OOM | Budget signal | Route to circuit breaker + GPU budget planner |
| `OperationCanceledException` | No retry | Propagate immediately |
| Unknown `OrtException` | Presumed permanent | Route to circuit breaker |

The retry handler is **injected as a decorator** around the adapter, not woven into each engine. This keeps engines testable without retry infra.

### Layer 2 ΓÇö Circuit Breaker (persistent/permanent failures)

A process-wide `InferenceCircuitBreaker` tracks failure state **per key** `(modelAlias, executionProviderKind)`:

```
State machine:

    ΓöîΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÉ
    Γöé                                          Γöé
    Γû╝    failure count ΓëÑ N (default: 3)        Γöé
  ΓöîΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÉ  within sliding window (5 min)  ΓöîΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÉ
  Γöé      Γöé ΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓû║ Γöé      Γöé
  Γöé OPEN Γöé                                 ΓöéHALF- Γöé
  Γöé      Γöé ΓùäΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇ Γöé OPEN Γöé
  ΓööΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÿ    cooldown timer expires       ΓööΓöÇΓöÇΓö¼ΓöÇΓöÇΓöÇΓöÇΓöÿ
    Γû▓         (default: 60 s)                 Γöé
    Γöé                                          Γöé
    Γöé        ΓöîΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÿ
    Γöé        Γöé  probe (adapter call) succeeds
    Γöé        Γû╝
    Γöé      ΓöîΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÉ
    ΓööΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöé      Γöé
           ΓöéCLOSEDΓöé
           Γöé      Γöé
           ΓööΓöÇΓöÇΓöÇΓöÇΓöÇΓöÇΓöÿ
```

- **CLOSED**: Normal operation. Failures increment a sliding-window counter.
- **OPEN**: All inference requests for this key are rejected immediately with `InferenceCircuitBreakerOpenException` without touching the model. The pipeline can log a `PipelineDegradationRecord` and attempt a fallback EP or degrade gracefully.
- **HALF-OPEN**: After cooldown, a single probe request is allowed through. Success ΓåÆ CLOSED (reset counter). Failure ΓåÆ OPEN (reset cooldown timer, possibly with backoff).
- **Manual reset**: The breaker exposes a `ResetAsync(key)` method for admin UI or model-reload scenarios.

The breaker state is **in-memory only** (no persistence across restarts). A restart resets all breakers to CLOSED.

### Layer 3 ΓÇö Budget-Aware Backoff (cross-cutting)

The retry and circuit-breaker layers consult an `IExecutionBudgetProvider` (designed in ADR-00XX GPU Memory Budget Planner) before attempting retries:

- If the GPU memory budget is exhausted, retry backoff doubles.
- If the budget indicates an OOM condition, the circuit breaker transitions to OPEN immediately for the affected model.
- Budget recovery (model unload) triggers `ResetAsync` on the affected circuit breaker keys.

### Integration Points

1. **Adapter Decorator**: `RetryingTtsEngineAdapter` wraps `ITtsEngineAdapter` with retry + circuit breaker before delegating to the real adapter.
2. **Registration**: Decorators are registered in DI via `TryDecorate` (Scrutor) or manual `AddSingleton<ITtsEngineAdapter>(sp => new RetryingAdapter(inner, breaker, budget))`.
3. **DegradationRecord**: When a circuit breaker opens, a `PipelineDegradationRecord` with code `INF_CIRCUIT_OPEN` and the breaker key is written. When a retry is exhausted, `INF_RETRY_EXHAUSTED` is written.
4. **Fallback**: The circuit breaker does not itself pick fallback EPs ΓÇö that remains the `RuntimePlanner`'s job. The breaker merely prevents immediate retry of a known-bad path.
5. **Pool integration**: `InferenceSessionPool` evicts sessions for a key when the circuit breaker opens for that key, ensuring stale sessions are not returned to callers.

## Consequences

### Positive

- Transient GPU/ONNX failures are absorbed transparently during pipeline runs.
- Persistent failures (corrupt model, incompatible EP) are detected and blocked from wasting resources within seconds.
- The circuit breaker state machine prevents repeated expensive model loads after a failure.
- The decorator pattern keeps engine implementations testable without retry/circuit infrastructure.
- Integration with `PipelineDegradationRecord` gives users visibility into inference reliability issues.

### Negative

- Adds complexity to the adapter registration path (decorator wiring).
- In-memory circuit breaker state is lost on process restart ΓÇö recovery after a permanent failure requires re-attempting the failing operation.
- Retries increase tail latency for the pipeline stage (max ~3.5 s additional latency in worst case before full exhaustion).
- Half-open probe requests that fail consume resources that a strictly-open breaker would have saved.

### Neutral

- The budget-aware backoff adds a dependency on the GPU memory budget planner (ADR-0012).
- Metrics (breaker state transitions, retry counts) should be exposed for diagnostics but no monitoring infra exists yet.
- The sliding window for failure counting requires a time-source dependency (use `ITimeSystem` or equivalent abstraction for testability).
