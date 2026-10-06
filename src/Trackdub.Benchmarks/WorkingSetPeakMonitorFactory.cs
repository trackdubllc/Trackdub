using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Lifetime handle for one continuous working-set peak sampling window.
/// The owner that starts the window (a stage boundary or an eval job) stops it;
/// the factory that created it owns how the sampler is driven.
/// </summary>
internal interface IWorkingSetPeakMonitor
{
    string? UnavailableReason { get; }

    /// <summary>
    /// Advisory warning when the sampling cadence dilated under load; null when ticks
    /// stayed within cadence. Informational only: it never changes the peak result.
    /// </summary>
    string? SamplingWarning { get; }

    long? Stop();
}

/// <summary>
/// Dedicated coordinator for peak-monitor lifetime. Progress capture and eval runners
/// create monitors through this factory instead of instantiating
/// <see cref="WorkingSetPeakMonitor"/> directly, so sampling policy (cadence default,
/// error handling, peak tracking) lives in one place.
/// </summary>
internal interface IWorkingSetPeakMonitorFactory
{
    IWorkingSetPeakMonitor Create(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval = null);
}

internal sealed class WorkingSetPeakMonitorFactory : IWorkingSetPeakMonitorFactory
{
    public IWorkingSetPeakMonitor Create(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval = null) =>
        new WorkingSetPeakMonitor(sampler, initialValue, interval);
}
