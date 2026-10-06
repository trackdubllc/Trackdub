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
        return ReadGpuMetric(
            reader.ReadAvailableVramMb,
            () => reader.UnavailableReason,
            "VRAM reader returned a negative reading.",
            "Free VRAM measurement unavailable");
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
        return ReadGpuMetric(
            reader.ReadDedicatedGpuMemoryBytes,
            () => reader.UnavailableReason,
            "Process GPU memory reader returned a negative reading.",
            "Process GPU memory measurement unavailable");
    }

    /// <summary>
    /// Shared read-validate-degrade pipeline for GPU metrics: a null reading reports the
    /// reader's own reason, a negative reading is a defective probe, and a failing query
    /// degrades the run's evidence instead of aborting the measurement.
    /// </summary>
    private static (long? Value, string? Reason) ReadGpuMetric(
        Func<long?> read,
        Func<string> unavailableReason,
        string negativeMessage,
        string failureMessage)
    {
        try
        {
            long? value = read();
            if (value is null)
            {
                return (null, unavailableReason());
            }

            // A lifted comparison would silently route a null reading down this branch.
            return value < 0
                ? (null, negativeMessage)
                : (value, null);
        }
        catch (Exception exception) when (IsPlatformReadFailure(exception) || exception is InvalidOperationException)
        {
            // A failing GPU query must degrade the run's evidence, never abort the measurement.
            return (null, $"{failureMessage} ({exception.GetType().Name}).");
        }
    }

    private static IProcessGpuMemoryReader DefaultProcessGpuReader { get; } = new UnavailableProcessGpuMemoryReader();

    private static bool IsPlatformReadFailure(Exception exception) =>
        exception is Win32Exception or NotSupportedException or UnauthorizedAccessException or
        // Native probes (PDH, DXGI) surface an unusable host this way; a measurement must degrade
        // rather than abort the run when a probe cannot be loaded or resolved.
        DllNotFoundException or EntryPointNotFoundException;
}
