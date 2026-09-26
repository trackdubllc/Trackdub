using System.Text.Json;
using Trackdub.Application.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Application.Tests;

public sealed class ResourceTelemetryValidatorTests
{
    private readonly ResourceTelemetryValidator validator = new();
    private static ResourceUsageSnapshot Start => new()
    {
        CpuTimeMilliseconds = 100,
        MonotonicMilliseconds = 1000,
        ProcessorCount = 4,
        WorkingSetBytes = 1000,
        ManagedAllocatedBytes = 100,
        AvailableVramMb = 500
    };
    private static ResourceUsageSnapshot End => new()
    {
        CpuTimeMilliseconds = 300,
        MonotonicMilliseconds = 1100,
        ProcessorCount = 4,
        WorkingSetBytes = 800,
        ManagedAllocatedBytes = 400,
        AvailableVramMb = 400
    };

    [Fact]
    public void Validate_normalizes_cpu_and_passes_inclusive_limits_using_endpoint_memory_maxima()
    {
        var result = validator.Validate(Start, End, new ResourceTelemetryBounds
        {
            MaxCpuPercent = 50,
            MaxWorkingSetBytes = 1000,
            MaxManagedAllocatedBytes = 300,
            MinAvailableVramMb = 400
        });
        Assert.Equal(ResourceTelemetryStatus.Passed, result.Status);
        Assert.Equal(200, result.CpuTimeMilliseconds);
        Assert.Equal(100, result.ElapsedMilliseconds);
        Assert.Equal(4, result.ProcessorCount);
        Assert.Collection(result.Checks,
            check => AssertCheck(check, "cpuPercent", 50),
            check => AssertCheck(check, "workingSetBytes", 1000),
            check => AssertCheck(check, "managedAllocatedBytes", 300),
            check => AssertCheck(check, "availableVramMb", 400));
    }

    [Fact]
    public void Validate_uses_measured_peak_working_set_when_it_exceeds_both_endpoints()
    {
        ResourceUsageSnapshot peak = End with { PeakWorkingSetBytes = 1500 };

        ResourceTelemetryValidation result = validator.Validate(Start, peak, new() { MaxWorkingSetBytes = 1200 });

        Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
        Assert.Equal(1500d, Check(result, "workingSetBytes").ObservedValue);
    }

    [Fact]
    public void Validate_reports_working_set_unavailable_when_continuous_sampling_failed()
    {
        ResourceUsageSnapshot failedPeak = End with
        {
            PeakWorkingSetUnavailableReason = "Continuous working-set sampling unavailable (InvalidOperationException)."
        };

        ResourceTelemetryValidation result = validator.Validate(Start, failedPeak, new() { MaxWorkingSetBytes = 1200 });

        Assert.Equal(ResourceTelemetryStatus.Unavailable, Check(result, "workingSetBytes").Status);
        Assert.Null(Check(result, "workingSetBytes").ObservedValue);
        Assert.Contains("InvalidOperationException", Check(result, "workingSetBytes").Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cpuPercent")]
    [InlineData("workingSetBytes")]
    [InlineData("managedAllocatedBytes")]
    [InlineData("availableVramMb")]
    public void Validate_breaching_each_bound_fails(string metric)
    {
        var bounds = metric switch
        {
            "cpuPercent" => new ResourceTelemetryBounds { MaxCpuPercent = 49 },
            "workingSetBytes" => new ResourceTelemetryBounds { MaxWorkingSetBytes = 999 },
            "managedAllocatedBytes" => new ResourceTelemetryBounds { MaxManagedAllocatedBytes = 299 },
            _ => new ResourceTelemetryBounds { MinAvailableVramMb = 401 }
        };
        var result = validator.Validate(Start, End, bounds);
        Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
        Assert.Equal(ResourceTelemetryStatus.Failed, Check(result, metric).Status);
        Assert.NotNull(Check(result, metric).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Validate_known_endpoint_breach_fails_even_if_other_endpoint_is_missing(bool missingStart)
    {
        var known = Start with { WorkingSetBytes = 200 };
        var result = validator.Validate(missingStart ? null : known, missingStart ? known : null,
            new() { MaxWorkingSetBytes = 100 });
        Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
        Assert.Equal(ResourceTelemetryStatus.Failed, Check(result, "workingSetBytes").Status);
        Assert.Equal(200d, Check(result, "workingSetBytes").ObservedValue);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, Check(result, "cpuPercent").Status);
    }

    [Fact]
    public void Validate_vram_floor_uses_the_end_reading_and_degrades_when_it_is_missing()
    {
        // A floor is breached by the end-of-stage headroom, not by either endpoint in isolation.
        var result = validator.Validate(Start, End, new() { MinAvailableVramMb = 400 });
        Assert.Equal(ResourceTelemetryStatus.Passed, result.Status);
        AssertCheck(Check(result, "availableVramMb"), "availableVramMb", 400);

        // Start headroom was higher, but only the end reading reflects pressure during the stage.
        Assert.Equal(500L, Start.AvailableVramMb!.Value);

        // Without an end reading there is nothing to compare, so the floor degrades rather than
        // silently passing or failing on a stale start value.
        ResourceTelemetryValidation noEnd = validator.Validate(Start, null, new() { MinAvailableVramMb = 400 });
        Assert.Equal(ResourceTelemetryStatus.Unavailable, Check(noEnd, "availableVramMb").Status);
        Assert.Null(Check(noEnd, "availableVramMb").ObservedValue);
    }

    [Fact]
    public void Validate_default_bounds_do_not_invent_budgets()
    {
        var result = validator.Validate(Start, End, new());
        Assert.Equal(ResourceTelemetryStatus.Passed, result.Status);
        Assert.All(result.Checks, check => Assert.Null(check.Threshold));
    }

    [Fact]
    public void Validate_zero_cpu_and_allocation_deltas_pass_zero_limits()
    {
        var result = validator.Validate(Start, End with
        { CpuTimeMilliseconds = 100, ManagedAllocatedBytes = 100 }, new()
        { MaxCpuPercent = 0, MaxManagedAllocatedBytes = 0 });
        Assert.Equal(ResourceTelemetryStatus.Passed, result.Status);
        Assert.Equal(0, Check(result, "cpuPercent").ObservedValue);
        Assert.Equal(0, Check(result, "managedAllocatedBytes").ObservedValue);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Validate_missing_snapshots_are_unavailable(bool missingStart, bool missingEnd)
    {
        var result = validator.Validate(missingStart ? null : Start, missingEnd ? null : End, new());
        Assert.Equal(ResourceTelemetryStatus.Unavailable, result.Status);
        // The free-VRAM floor reads only the end-of-stage sample, so it stays evaluable whenever
        // that sample exists; the delta metrics need both endpoints and degrade without them.
        ResourceTelemetryCheck vram = Check(result, "availableVramMb");
        if (missingEnd)
        {
            Assert.Equal(ResourceTelemetryStatus.Unavailable, vram.Status);
            Assert.Null(vram.ObservedValue);
            Assert.NotNull(vram.Reason);
            Assert.All(result.Checks, check => Assert.Equal(ResourceTelemetryStatus.Unavailable, check.Status));
        }
        else
        {
            Assert.Equal(ResourceTelemetryStatus.Passed, vram.Status);
            Assert.Equal(400d, vram.ObservedValue);
            Assert.All(result.Checks.Where(check => check.Metric != "availableVramMb"), check =>
            {
                Assert.Equal(ResourceTelemetryStatus.Unavailable, check.Status);
                Assert.Null(check.ObservedValue);
                Assert.NotNull(check.Reason);
            });
        }
    }

    [Fact]
    public void Validate_missing_metrics_preserves_independent_measurements_and_exact_vram_reason()
    {
        const string reason = "No VRAM reader is registered for this host.";
        var result = validator.Validate(Start, End with
        {
            CpuTimeMilliseconds = null,
            CpuUnavailableReason = "CPU unavailable",
            AvailableVramMb = null,
            VramUnavailableReason = reason
        }, new());
        Assert.Equal(ResourceTelemetryStatus.Unavailable, result.Status);
        Assert.Equal("CPU unavailable", Check(result, "cpuPercent").Reason);
        Assert.Equal(reason, Check(result, "availableVramMb").Reason);
        Assert.Null(Check(result, "availableVramMb").ObservedValue);
        AssertCheck(Check(result, "workingSetBytes"), "workingSetBytes", 1000);
        AssertCheck(Check(result, "managedAllocatedBytes"), "managedAllocatedBytes", 300);
    }

    [Fact]
    public void Validate_missing_memory_is_not_zero_and_failure_dominates_unavailable()
    {
        var result = validator.Validate(Start, End with
        { WorkingSetBytes = null, MemoryUnavailableReason = "memory unavailable", AvailableVramMb = null },
            new() { MaxCpuPercent = 49 });
        Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
        Assert.Equal("memory unavailable", Check(result, "workingSetBytes").Reason);
        Assert.Null(Check(result, "workingSetBytes").ObservedValue);
        AssertCheck(Check(result, "managedAllocatedBytes"), "managedAllocatedBytes", 300);
    }

    [Fact]
    public void Validate_zero_elapsed_is_unavailable_not_infinite()
    {
        var result = validator.Validate(Start, End with { MonotonicMilliseconds = 1000 }, new());
        Assert.Equal(ResourceTelemetryStatus.Unavailable, Check(result, "cpuPercent").Status);
        Assert.Equal(0, result.ElapsedMilliseconds);
        Assert.Null(Check(result, "cpuPercent").ObservedValue);
        Assert.NotNull(Check(result, "cpuPercent").Reason);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void Validate_invalid_cpu_samples_fail_without_nonfinite_json(double value)
    {
        foreach (bool invalidStart in new[] { true, false })
        {
            var result = validator.Validate(invalidStart ? Start with { CpuTimeMilliseconds = value } : Start,
                invalidStart ? End : End with { CpuTimeMilliseconds = value }, new());
            Assert.Equal(ResourceTelemetryStatus.Failed, Check(result, "cpuPercent").Status);
            Assert.Null(Check(result, "cpuPercent").ObservedValue);
            Assert.NotNull(Check(result, "cpuPercent").Reason);
            Assert.NotEmpty(JsonSerializer.Serialize(result));
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Validate_nonfinite_timestamps_fail_without_nonfinite_json(double value)
    {
        foreach (bool invalidStart in new[] { true, false })
        {
            var result = validator.Validate(invalidStart ? Start with { MonotonicMilliseconds = value } : Start,
                invalidStart ? End : End with { MonotonicMilliseconds = value }, new());
            Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
            Assert.Null(Check(result, "cpuPercent").ObservedValue);
            Assert.Null(result.ElapsedMilliseconds);
            Assert.NotEmpty(JsonSerializer.Serialize(result));
        }
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("cpu")]
    [InlineData("processorsZero")]
    [InlineData("processorsNegative")]
    [InlineData("processorsChanged")]
    [InlineData("allocation")]
    [InlineData("negativeAllocation")]
    [InlineData("negativeWorkingSet")]
    [InlineData("negativeVram")]
    public void Validate_regressions_and_invalid_counters_fail(string defect)
    {
        var end = defect switch
        {
            "clock" => End with { MonotonicMilliseconds = 999 },
            "cpu" => End with { CpuTimeMilliseconds = 99 },
            "processorsZero" => End with { ProcessorCount = 0 },
            "processorsNegative" => End with { ProcessorCount = -1 },
            "processorsChanged" => End with { ProcessorCount = 8 },
            "allocation" => End with { ManagedAllocatedBytes = 99 },
            "negativeAllocation" => End with { ManagedAllocatedBytes = -1 },
            "negativeWorkingSet" => End with { WorkingSetBytes = -1 },
            _ => End with { AvailableVramMb = -1 }
        };
        var result = validator.Validate(Start, end, new());
        Assert.Equal(ResourceTelemetryStatus.Failed, result.Status);
        Assert.Contains(result.Checks, check => check.Status == ResourceTelemetryStatus.Failed && check.Reason != null);
    }

    [Fact]
    public void Validate_vram_floor_compares_long_values_exactly()
    {
        // A floor one MB above the reading must fail; the exact reading must pass. Comparing as
        // integers avoids the rounding a double conversion would introduce at this magnitude.
        var result = validator.Validate(Start,
            End with { AvailableVramMb = long.MaxValue - 1 }, new() { MinAvailableVramMb = long.MaxValue });
        Assert.Equal(ResourceTelemetryStatus.Failed, Check(result, "availableVramMb").Status);

        var exact = validator.Validate(Start,
            End with { AvailableVramMb = long.MaxValue }, new() { MinAvailableVramMb = long.MaxValue });
        Assert.Equal(ResourceTelemetryStatus.Passed, Check(exact, "availableVramMb").Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Validate_rejects_invalid_cpu_bounds(double maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => validator.Validate(Start, End,
            new() { MaxCpuPercent = maximum }));
    }

    [Fact]
    public void Validate_rejects_negative_byte_bounds_even_without_samples()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => validator.Validate(null, null, new() { MaxWorkingSetBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => validator.Validate(null, null, new() { MaxManagedAllocatedBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => validator.Validate(null, null, new() { MinAvailableVramMb = -1 }));
    }

    private static ResourceTelemetryCheck Check(ResourceTelemetryValidation result, string metric) =>
        Assert.Single(result.Checks, check => check.Metric == metric);

    private static void AssertCheck(ResourceTelemetryCheck check, string metric, double observed)
    {
        Assert.Equal(metric, check.Metric);
        Assert.Equal(ResourceTelemetryStatus.Passed, check.Status);
        Assert.Equal(observed, check.ObservedValue);
    }
}
