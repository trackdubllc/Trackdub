// SKETCH — C# caller side. Deliberately a .sketch.cs so it NEVER compiles.
// Shows how the existing Trackdub.Inference stage abstractions would drive the
// sidecar without leaking process/IPC details upward (Domain keeps depending
// on nothing; this would live behind Infrastructure/Composition).

using System.Diagnostics;
using System.Text.Json;

/*
public sealed record SidecarTensor(string Dtype, long[] Shape, byte[] Data);
public sealed record SidecarLoadResult(bool Loaded, string ActiveProvider, long LoadMs);
public sealed record SidecarInferResult(
    bool Ok, Dictionary<string, SidecarTensor> Outputs, long InferMs);

// The sidecar executes a planner-produced plan — it never selects models or
// providers itself. Mirrors StageRuntimePlan: the model path is already
// integrity-qualified, the provider list is the planner's ordered fallback,
// and RequirePreferredExecutionProvider (hard-pin) is honored, not retried.
public sealed record SidecarLoadPlan(
    string ModelPath, IReadOnlyList<string> Providers, bool RequirePreferred);

public interface IInferenceSidecar : IAsyncDisposable
{
    // Lifecycle: start once (app launch / first use), warm for the session.
    Task StartAsync(CancellationToken ct);

    // Distinct readiness states — mirrors the honest-readiness rule:
    // process alive != model loaded != accelerator settled.
    Task<bool> IsAliveAsync(CancellationToken ct);
    Task<SidecarLoadResult> LoadAsync(SidecarLoadPlan plan, CancellationToken ct);
    Task<SidecarInferResult> InferAsync(
        string id, IReadOnlyDictionary<string, SidecarTensor> inputs, CancellationToken ct);
}

// Sketch implementation notes (not code):
// - Process.Start("trackdub-sidecar") with stdin/stdout redirected; JSON-lines.
// - Supervision owned by Composition root (e.g. ISidecarHost in Infrastructure):
//   restart-on-crash with backoff, kill-on-shutdown, version stamp check so a
//   stale sidecar binary can never serve a new frontend (same fingerprint
//   discipline as EpContextArtifact stamps / smoke verdicts).
// - ActiveProvider from LoadAsync flows into stage evidence verbatim, so the UI
//   can say "ran on DirectML (fell back from TensorRT-RTX)" instead of "GPU".
// - Phase 2 (Python for .pt-only models, IF governance ever allows it — see
//   pitch Q4/Q7) implements THIS interface — no caller changes, only registration.
*/
