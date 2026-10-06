using Trackdub.Benchmarks;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Each predicate preserves the exact exception set its call site used inline before
/// consolidation; these tests lock the accept/reject boundary so a future merge of
/// the sets cannot silently change telemetry behavior.
/// </summary>
public sealed class TelemetryExceptionFiltersTests
{
    public static TheoryData<Exception, bool> WorkingSetCases => new()
    {
        { new ObjectDisposedException("sampler"), true },
        { new InvalidOperationException(), true },
        { new NotSupportedException(), true },
        { new System.ComponentModel.Win32Exception(), true },
        { new UnauthorizedAccessException(), true },
        { new System.Security.SecurityException(), true },
        { new IOException(), false },
        { new ArgumentException(), false },
    };

    public static TheoryData<Exception, bool> CollectorCases => new()
    {
        { new System.ComponentModel.Win32Exception(), true },
        { new NotSupportedException(), true },
        { new UnauthorizedAccessException(), true },
        { new InvalidOperationException(), true },
        { new IOException(), true },
        // ObjectDisposedException derives from InvalidOperationException, so the original
        // inline filter caught it too; the predicate preserves that.
        { new ObjectDisposedException("collector"), true },
        { new System.Security.SecurityException(), false },
        { new ArgumentException(), false },
    };

    public static TheoryData<Exception, bool> ProcessCaptureCases => new()
    {
        { new System.ComponentModel.Win32Exception(), true },
        { new NotSupportedException(), true },
        { new InvalidOperationException(), true },
        { new UnauthorizedAccessException(), false },
        { new IOException(), false },
        // ObjectDisposedException derives from InvalidOperationException, so the original
        // inline filter caught it too; the predicate preserves that.
        { new ObjectDisposedException("process"), true },
        { new ArgumentException(), false },
    };

    [Theory]
    [MemberData(nameof(WorkingSetCases))]
    public void Working_set_filter_matches_its_original_set(Exception exception, bool expected) =>
        Assert.Equal(expected, TelemetryExceptionFilters.IsWorkingSetSamplingFailure(exception));

    [Theory]
    [MemberData(nameof(CollectorCases))]
    public void Collector_filter_matches_its_original_set(Exception exception, bool expected) =>
        Assert.Equal(expected, TelemetryExceptionFilters.IsCollectorSamplingFailure(exception));

    [Theory]
    [MemberData(nameof(ProcessCaptureCases))]
    public void Process_capture_filter_matches_its_original_set(Exception exception, bool expected) =>
        Assert.Equal(expected, TelemetryExceptionFilters.IsProcessCaptureFailure(exception));
}
