using System.ComponentModel;
using System.Diagnostics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Captures the current process only, including concurrent work and excluding child processes.
/// Working set is a point-in-time sample, not a continuous peak measurement.
/// </summary>
public sealed class ProcessResourceTelemetryCollector(IAvailableVramReader? vramReader = null)
    : IResourceTelemetryCollector
{
    public ResourceUsageSnapshot Capture()
    {
        double? cpu = null;
        long? workingSet = null;
        string? cpuReason = null;
        string? memoryReason = null;

        // Separate process handles/read boundaries keep one unavailable metric from hiding another.
        try
        {
            using Process process = Process.GetCurrentProcess();
            cpu = process.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception exception) when (IsPlatformReadFailure(exception))
        {
            cpuReason = "Process CPU measurement unavailable on this platform or denied by the operating system.";
        }
        double timestamp = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

        try
        {
            using Process process = Process.GetCurrentProcess();
            workingSet = process.WorkingSet64;
        }
        catch (Exception exception) when (IsPlatformReadFailure(exception))
        {
            memoryReason = "Process working set measurement unavailable on this platform or denied by the operating system.";
        }

        (long? availableVramMb, string? vramReason) = ReadAvailableVram();

        return new ResourceUsageSnapshot
        {
            CpuTimeMilliseconds = cpu,
            MonotonicMilliseconds = timestamp,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = workingSet,
            ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            AvailableVramMb = availableVramMb,
            CpuUnavailableReason = cpuReason,
            MemoryUnavailableReason = memoryReason,
            VramUnavailableReason = vramReason
        };
    }

    private (long? Value, string? Reason) ReadAvailableVram()
    {
        IAvailableVramReader reader = vramReader ?? DefaultReader;
        try
        {
            long? value = reader.ReadAvailableVramMb();
            if (value is null)
            {
                return (null, reader.UnavailableReason);
            }

            // A lifted comparison would silently route a null reading down this branch.
            return value < 0
                ? (null, "VRAM reader returned a negative reading.")
                : (value, null);
        }
        catch (Exception exception) when (IsPlatformReadFailure(exception) || exception is InvalidOperationException)
        {
            // A failing GPU query must degrade the run's evidence, never abort the measurement.
            return (null, $"Free VRAM measurement unavailable ({exception.GetType().Name}).");
        }
    }

    private static IAvailableVramReader DefaultReader { get; } = new UnavailableAvailableVramReader();

    private static bool IsPlatformReadFailure(Exception exception) =>
        exception is Win32Exception or NotSupportedException or UnauthorizedAccessException;
}
