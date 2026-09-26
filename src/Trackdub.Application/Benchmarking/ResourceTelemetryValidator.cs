using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Application.Benchmarking;

public sealed class ResourceTelemetryValidator : IResourceTelemetryValidator
{
    public ResourceTelemetryValidation Validate(
        ResourceUsageSnapshot? start,
        ResourceUsageSnapshot? end,
        ResourceTelemetryBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.MaxCpuPercent is { } cpuBound && (!double.IsFinite(cpuBound) || cpuBound < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "CPU limit must be finite and nonnegative.");
        }
        if (bounds.MaxWorkingSetBytes < 0 || bounds.MaxManagedAllocatedBytes < 0 || bounds.MinAvailableVramMb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Byte and VRAM limits must be nonnegative.");
        }

        double? cpuTime = Difference(start?.CpuTimeMilliseconds, end?.CpuTimeMilliseconds);
        double? elapsed = Difference(start?.MonotonicMilliseconds, end?.MonotonicMilliseconds);
        int? processors = start is { ProcessorCount: > 0 } && end?.ProcessorCount == start.ProcessorCount
            ? start.ProcessorCount : null;
        ResourceTelemetryCheck[] checks =
        [
            CheckCpu(start, end, cpuTime, elapsed, processors, bounds.MaxCpuPercent),
            CheckWorkingSet(start, end, bounds.MaxWorkingSetBytes),
            CheckBytes("managedAllocatedBytes", start?.ManagedAllocatedBytes, end?.ManagedAllocatedBytes,
                bounds.MaxManagedAllocatedBytes, true, start?.MemoryUnavailableReason, end?.MemoryUnavailableReason),
            CheckMinimum("availableVramMb", end?.AvailableVramMb, bounds.MinAvailableVramMb,
                end?.VramUnavailableReason)
        ];
        return new ResourceTelemetryValidation
        {
            Status = checks.Any(check => check.Status == ResourceTelemetryStatus.Failed)
                ? ResourceTelemetryStatus.Failed
                : checks.Any(check => check.Status == ResourceTelemetryStatus.Unavailable)
                    ? ResourceTelemetryStatus.Unavailable : ResourceTelemetryStatus.Passed,
            CpuTimeMilliseconds = cpuTime,
            ElapsedMilliseconds = elapsed,
            ProcessorCount = processors,
            Checks = Array.AsReadOnly(checks)
        };
    }

    private static ResourceTelemetryCheck CheckCpu(
        ResourceUsageSnapshot? start, ResourceUsageSnapshot? end,
        double? cpuTime, double? elapsed, int? processors, double? maximum)
    {
        const string metric = "cpuPercent";
        if (InvalidCpu(start?.CpuTimeMilliseconds) || InvalidCpu(end?.CpuTimeMilliseconds))
        {
            return Failed(metric, maximum, "CPU counters must be finite and nonnegative.");
        }
        if (Nonfinite(start?.MonotonicMilliseconds) || Nonfinite(end?.MonotonicMilliseconds))
        {
            return Failed(metric, maximum, "Monotonic timestamps must be finite.");
        }
        if (start?.CpuTimeMilliseconds is null || end?.CpuTimeMilliseconds is null)
        {
            string? unavailableReason = start?.CpuTimeMilliseconds is null
                ? start?.CpuUnavailableReason
                : end?.CpuUnavailableReason;
            return Unavailable(metric, maximum, unavailableReason, "CPU counter sample unavailable.");
        }
        if (start is { ProcessorCount: <= 0 } || end is { ProcessorCount: <= 0 })
        {
            return Failed(metric, maximum, "Processor count must be positive.");
        }
        if (start.ProcessorCount != end.ProcessorCount)
        {
            return Failed(metric, maximum, "Processor count changed between samples.");
        }
        if (cpuTime < 0)
        {
            return Failed(metric, maximum, "CPU counter regressed between samples.");
        }
        if (elapsed < 0)
        {
            return Failed(metric, maximum, "Monotonic timestamp regressed between samples.");
        }
        if (start.MonotonicMilliseconds is null || end.MonotonicMilliseconds is null)
        {
            return Unavailable(metric, maximum, null, "Monotonic timestamp sample unavailable.");
        }
        if (!cpuTime.HasValue || !elapsed.HasValue)
        {
            return Failed(metric, maximum, "CPU or elapsed delta is not finite.");
        }
        if (elapsed == 0)
        {
            return Unavailable(metric, maximum, null, "Elapsed time is zero.");
        }

        // Divide before scaling to avoid overflowing a finite CPU-time numerator.
        double observed = cpuTime.Value / elapsed.Value / processors!.Value * 100;
        if (!double.IsFinite(observed))
        {
            return Failed(metric, maximum, "Normalized CPU percentage is not finite.");
        }
        bool exceeded = maximum.HasValue && observed > maximum.Value;
        return new ResourceTelemetryCheck(metric,
            exceeded ? ResourceTelemetryStatus.Failed : ResourceTelemetryStatus.Passed,
            observed, maximum, exceeded ? "Configured upper bound exceeded." : null);
    }

    private static ResourceTelemetryCheck CheckWorkingSet(
        ResourceUsageSnapshot? start, ResourceUsageSnapshot? end, long? maximum)
    {
        long? startWorkingSet = start?.WorkingSetBytes;
        long? endWorkingSet = end?.WorkingSetBytes;
        if (startWorkingSet is < 0 || endWorkingSet is < 0)
        {
            return Failed("workingSetBytes", maximum, "Working-set counters must be nonnegative.");
        }

        long? knownPeak = end?.PeakWorkingSetBytes;
        if (knownPeak is < 0)
        {
            return Failed("workingSetBytes", maximum, "Sampled peak working set must be nonnegative.");
        }
        if (!knownPeak.HasValue && end is not null && !string.IsNullOrWhiteSpace(end.PeakWorkingSetUnavailableReason))
        {
            long? knownEndpoint = Maximum(startWorkingSet, endWorkingSet);
            if (knownEndpoint.HasValue && maximum.HasValue && knownEndpoint.Value > maximum.Value)
            {
                return new("workingSetBytes", ResourceTelemetryStatus.Failed, knownEndpoint.Value, maximum,
                    "Available endpoint exceeds the configured upper bound; continuous peak sampling was unavailable.");
            }
            return Unavailable("workingSetBytes", maximum, end.PeakWorkingSetUnavailableReason,
                "Continuous working-set sampling unavailable.");
        }

        bool bothEndpointsKnown = startWorkingSet.HasValue && endWorkingSet.HasValue;
        long? observed = knownPeak.HasValue || bothEndpointsKnown
            ? Maximum(Maximum(startWorkingSet, endWorkingSet), knownPeak)
            : null;
        if (!observed.HasValue)
        {
            long? knownEndpoint = Maximum(startWorkingSet, endWorkingSet);
            if (knownEndpoint.HasValue && maximum.HasValue && knownEndpoint.Value > maximum.Value)
            {
                return new("workingSetBytes", ResourceTelemetryStatus.Failed, knownEndpoint.Value, maximum,
                    "Available endpoint exceeds the configured upper bound; the other endpoint is unavailable.");
            }
            return Unavailable("workingSetBytes", maximum,
                startWorkingSet is null ? start?.MemoryUnavailableReason : end?.MemoryUnavailableReason,
                "Working-set sample unavailable.");
        }

        bool exceeded = maximum.HasValue && observed.Value > maximum.Value;
        return new ResourceTelemetryCheck("workingSetBytes",
            exceeded ? ResourceTelemetryStatus.Failed : ResourceTelemetryStatus.Passed,
            observed.Value, maximum, exceeded ? "Configured upper bound exceeded." : null);
    }

    private static long? Maximum(long? first, long? second) =>
        first.HasValue && second.HasValue ? Math.Max(first.Value, second.Value) : first ?? second;

    private static ResourceTelemetryCheck CheckBytes(
        string metric, long? start, long? end, long? maximum, bool cumulative,
        string? startReason, string? endReason)
    {
        if (start < 0 || end < 0)
        {
            return Failed(metric, maximum, "Byte counters must be nonnegative.");
        }
        if (!start.HasValue || !end.HasValue)
        {
            long? known = start ?? end;
            if (!cumulative && known.HasValue && maximum.HasValue && known.Value > maximum.Value)
            {
                return new(metric, ResourceTelemetryStatus.Failed, known.Value, maximum,
                    "Available endpoint exceeds the configured upper bound; the other endpoint is unavailable.");
            }
            return Unavailable(metric, maximum, !start.HasValue ? startReason : endReason,
                "Byte counter sample unavailable.");
        }
        if (cumulative && end.Value < start.Value)
        {
            return Failed(metric, maximum, "Managed allocation counter regressed between samples.");
        }
        long observed = cumulative ? end.Value - start.Value : Math.Max(start.Value, end.Value);
        // Compare as integers before converting for the shared evidence representation.
        bool exceeded = maximum.HasValue && observed > maximum.Value;
        return new ResourceTelemetryCheck(metric,
            exceeded ? ResourceTelemetryStatus.Failed : ResourceTelemetryStatus.Passed,
            observed, maximum, exceeded ? "Configured upper bound exceeded." : null);
    }

    /// <summary>
    /// Validates the free-VRAM floor. Unlike the usage metrics this is a minimum, so a run that
    /// leaves too little headroom fails even though its observed value is small.
    /// </summary>
    private static ResourceTelemetryCheck CheckMinimum(
        string metric, long? observed, long? minimum, string? unavailableReason)
    {
        if (observed < 0)
        {
            return Failed(metric, minimum, "Free VRAM reading must be nonnegative.");
        }

        if (!observed.HasValue)
        {
            return Unavailable(metric, minimum, unavailableReason, "Free VRAM reading unavailable.");
        }

        bool breached = minimum.HasValue && observed.Value < minimum.Value;
        return new ResourceTelemetryCheck(metric,
            breached ? ResourceTelemetryStatus.Failed : ResourceTelemetryStatus.Passed,
            observed.Value, minimum, breached ? "Free VRAM fell below the configured floor." : null);
    }

    private static double? Difference(double? start, double? end)
    {
        if (start is not { } first || end is not { } last || !double.IsFinite(first) || !double.IsFinite(last))
        {
            return null;
        }
        double difference = last - first;
        return double.IsFinite(difference) ? difference : null;
    }

    private static bool InvalidCpu(double? value) => value is { } counter && (!double.IsFinite(counter) || counter < 0);
    private static bool Nonfinite(double? value) => value is { } number && !double.IsFinite(number);

    private static ResourceTelemetryCheck Failed(string metric, double? maximum, string reason) =>
        new(metric, ResourceTelemetryStatus.Failed, null, maximum, reason);

    private static ResourceTelemetryCheck Unavailable(string metric, double? maximum, string? reason, string fallback) =>
        new(metric, ResourceTelemetryStatus.Unavailable, null, maximum, reason ?? fallback);
}
