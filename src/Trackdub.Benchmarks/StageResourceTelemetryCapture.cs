using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks;

public sealed class StageResourceTelemetryCapture(
    IResourceTelemetryCollector collector,
    IResourceTelemetryValidator validator,
    ResourceTelemetryBounds bounds,
    string phase,
    int iteration,
    IProgress<PipelineProgressEvent>? timing = null) : IProgress<PipelineProgressEvent>
{
    private readonly object gate = new();
    private readonly Dictionary<string, PendingStage> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BenchmarkStageResourceTelemetry> samples = [];

    public void Report(PipelineProgressEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (value.EventKind == PipelineProgressEventKind.Started)
            {
                // Nested workflows share the enclosing stage key but have their own terminal events.
                if (pending.TryGetValue(value.StageKey, out PendingStage? existing))
                {
                    pending[value.StageKey] = existing with { Depth = existing.Depth + 1 };
                    return;
                }
                int attempt = attempts.GetValueOrDefault(value.StageKey) + 1;
                attempts[value.StageKey] = attempt;
                pending[value.StageKey] = new(value.StageName, attempt, Capture());
                completed.Remove(value.StageKey);
            }
            else if (value.EventKind is PipelineProgressEventKind.Completed or PipelineProgressEventKind.Failed or PipelineProgressEventKind.Skipped)
            {
                if (completed.Contains(value.StageKey)) return;
                if (pending.TryGetValue(value.StageKey, out PendingStage? nested) && nested.Depth > 1)
                {
                    pending[value.StageKey] = nested with { Depth = nested.Depth - 1 };
                    return;
                }
                pending.Remove(value.StageKey, out PendingStage? start);
                var status = value.EventKind switch
                {
                    PipelineProgressEventKind.Completed => BenchmarkEvidenceStatus.Completed,
                    PipelineProgressEventKind.Skipped => BenchmarkEvidenceStatus.Skipped,
                    _ => BenchmarkEvidenceStatus.Failed,
                };
                Record(value.StageKey, start, status, value.Message);
                completed.Add(value.StageKey);
            }
            timing?.Report(value);
        }
    }

    public void CompleteOutcomes(IReadOnlyList<StageOutcome> outcomes)
    {
        lock (gate)
        {
            foreach (StageOutcome outcome in outcomes)
            {
                var status = outcome.Status switch
                {
                    StageStatus.Succeeded => BenchmarkEvidenceStatus.Completed,
                    StageStatus.Skipped => BenchmarkEvidenceStatus.Skipped,
                    StageStatus.PartiallySucceeded => BenchmarkEvidenceStatus.PartiallyCompleted,
                    _ => BenchmarkEvidenceStatus.Failed,
                };
                string? pendingKey = pending.FirstOrDefault(pair =>
                    pair.Key.Equals(outcome.StageName, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Name.Equals(outcome.StageName, StringComparison.OrdinalIgnoreCase)).Key;
                if (pendingKey is not null)
                {
                    pending.Remove(pendingKey, out PendingStage? start);
                    Record(outcome.StageName, start, status, outcome.ReasonCode);
                    completed.Add(pendingKey);
                }
                else
                {
                    int last = samples.FindLastIndex(sample => sample.Stage.Equals(outcome.StageName, StringComparison.OrdinalIgnoreCase));
                    if (last >= 0)
                    {
                        samples[last] = samples[last] with
                        {
                            ExecutionStatus = status,
                            Reason = CombineReason(samples[last].Reason, outcome.ReasonCode),
                        };
                    }
                    else
                    {
                        Record(outcome.StageName, null, status, outcome.ReasonCode);
                    }
                }
            }
        }
    }

    public void CompletePending(BenchmarkEvidenceStatus status, string reason)
    {
        lock (gate)
        {
            foreach ((string key, PendingStage start) in pending)
            {
                Record(key, start, status, reason);
                completed.Add(key);
            }
            pending.Clear();
        }
    }

    public IReadOnlyList<BenchmarkStageResourceTelemetry> Snapshot()
    {
        lock (gate) return samples.ToArray();
    }

    /// <summary>
    /// Keeps the progress message that names what actually happened, while leaving the structured
    /// reason code queryable. Reconciliation must not replace a specific cause with a bare code.
    /// </summary>
    private static string? CombineReason(string? reason, string? code)
    {
        bool hasReason = !string.IsNullOrWhiteSpace(reason);
        bool hasCode = !string.IsNullOrWhiteSpace(code);
        if (hasReason && hasCode)
        {
            return reason!.Contains(code!, StringComparison.OrdinalIgnoreCase) ? reason : $"{code}: {reason}";
        }

        return hasCode ? code : hasReason ? reason : null;
    }

    private void Record(string stage, PendingStage? start, BenchmarkEvidenceStatus status, string? reason)
    {
        ResourceTelemetryValidation validation = validator.Validate(start?.Snapshot, start is null ? null : Capture(), bounds);
        if (start is null)
        {
            string missingReason = reason ?? (status == BenchmarkEvidenceStatus.Skipped
                ? "Stage skipped before telemetry capture."
                : "Stage produced no resource boundary samples.");
            ResourceTelemetryStatus missingStatus = status == BenchmarkEvidenceStatus.Skipped
                ? ResourceTelemetryStatus.Skipped : ResourceTelemetryStatus.Unavailable;
            validation = validation with
            {
                Status = missingStatus,
                Checks = validation.Checks.Select(check => check with { Status = missingStatus, Reason = missingReason }).ToArray(),
            };
            reason ??= missingReason;
        }
        samples.Add(new()
        {
            Stage = stage,
            Phase = phase,
            Iteration = iteration,
            Attempt = start?.Attempt ?? 0,
            ExecutionStatus = status,
            Reason = reason,
            Validation = validation,
        });
    }

    private ResourceUsageSnapshot Capture()
    {
        try
        {
            return collector.Capture();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or
            UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            string reason = $"Resource sampling unavailable ({ex.GetType().Name}).";
            return new()
            {
                CpuUnavailableReason = reason,
                MemoryUnavailableReason = reason,
                VramUnavailableReason = reason,
            };
        }
    }

    private sealed record PendingStage(string Name, int Attempt, ResourceUsageSnapshot Snapshot, int Depth = 1);
}
