using System.ComponentModel;
using System.Diagnostics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Captures the current process only, including concurrent work and excluding child processes.
/// Working set is a point-in-time sample; interval peaks are measured by <see cref="IWorkingSetSampler"/>.
/// </summary>
public sealed class ProcessResourceTelemetryCollector(
    IAvailableVramReader? vramReader = null,
    IProcessGpuMemoryReader? processGpuMemoryReader = null)
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
        (long? gpuBytes, string? gpuReason) = ReadProcessGpuMemory();

        return new ResourceUsageSnapshot
        {
            CpuTimeMilliseconds = cpu,
            MonotonicMilliseconds = timestamp,
            ProcessorCount = Environment.ProcessorCount,
            WorkingSetBytes = workingSet,
            ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
            AvailableVramMb = availableVramMb,
            GpuBytes = gpuBytes,
            CpuUnavailableReason = cpuReason,
            MemoryUnavailableReason = memoryReason,
            VramUnavailableReason = vramReason,
            GpuUnavailableReason = gpuReason
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
        catch (Exception exception) when (IsGpuReadFailure(exception))
        {
            // Expected GPU query failures degrade the run's evidence without aborting the measurement.
            return (null, $"Free VRAM measurement unavailable ({exception.GetType().Name}).");
        }
    }

    private static IAvailableVramReader DefaultReader { get; } = new UnavailableAvailableVramReader();

    /// <summary>
    /// Reads the process-isolated dedicated GPU footprint. Kept separate from
    /// <see cref="ReadAvailableVram"/> so an unavailable adapter headroom reading can never
    /// suppress an available per-process attribution, and vice versa.
    /// </summary>
    private (long? Value, string? Reason) ReadProcessGpuMemory()
    {
        IProcessGpuMemoryReader reader = processGpuMemoryReader ?? DefaultProcessGpuReader;
        try
        {
            long? value = reader.ReadDedicatedGpuMemoryBytes();
            if (value is null)
            {
                return (null, reader.UnavailableReason);
            }

            // A lifted comparison would silently route a null reading down this branch.
            return value < 0
                ? (null, "Process GPU memory reader returned a negative reading.")
                : (value, null);
        }
        catch (Exception exception) when (IsGpuReadFailure(exception))
        {
            // Expected GPU query failures degrade the run's evidence without aborting the measurement.
            return (null, $"Process GPU memory measurement unavailable ({exception.GetType().Name}).");
        }
    }

    private static IProcessGpuMemoryReader DefaultProcessGpuReader { get; } = new UnavailableProcessGpuMemoryReader();

    private static bool IsPlatformReadFailure(Exception exception) =>
        exception is Win32Exception or NotSupportedException or UnauthorizedAccessException;

    /// <summary>
    /// GPU probes additionally load native DLLs (PDH, DXGI) that the BCL process reads never
    /// touch: a missing library or export surfaces an unusable host and must degrade the reading
    /// rather than abort the measurement. Kept separate from
    /// <see cref="IsPlatformReadFailure"/> so those failures cannot silently mask themselves as
    /// benign platform unavailability in the unrelated CPU and working-set reads.
    /// </summary>
    private static bool IsGpuReadFailure(Exception exception) =>
        IsPlatformReadFailure(exception) ||
        exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException;
}
