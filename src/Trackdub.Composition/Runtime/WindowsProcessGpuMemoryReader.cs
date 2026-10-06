using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Reads this process's dedicated GPU memory from the Windows "GPU Process Memory" performance
/// counter set.
/// </summary>
/// <remarks>
/// <para>
/// Windows publishes that counter set per process: each instance is named
/// <c>pid_&lt;pid&gt;_luid_&lt;high&gt;_&lt;low&gt;_phys_&lt;n&gt;</c>, so its "Dedicated Usage"
/// counter is exactly the process-isolated allocation <see cref="IAvailableVramReader"/> cannot
/// attribute. A process with instances on several adapters (or on several physical partitions of
/// a linked display adapter) reports one instance each, so the reading is their sum.
/// </para>
/// <para>
/// PDH is called through <c>pdh.dll</c> directly rather than through a managed package so
/// composition keeps its existing zero-extra-dependency posture, matching the hand-written DXGI
/// P/Invoke in the inference layer. A host without the counter set (no GPU, a server SKU without
/// GPU counters, or a driver that does not publish them) reports an explicit unavailable result.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessGpuMemoryReader : IProcessGpuMemoryReader
{
    private const string CounterSetName = "GPU Process Memory";
    private const string DedicatedUsageCounterName = "Dedicated Usage";

    /// <summary>PDH_FMT_LARGE: return the counter as a 64-bit value rather than a double.</summary>
    private const uint FormatLarge = 0x0000_0100;

    private const uint ErrorSuccess = 0;
    private const uint MoreData = 0x8000_07D2;
    private const uint ValidDataStatus = 0;
    private const uint NewDataStatus = 1;

    private static readonly Lazy<bool> Warmup = new(
        WarmCounterSubsystem, LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile string? warmupFailure;

    public WindowsProcessGpuMemoryReader() => _ = Warmup.Value;

    /// <summary>
    /// Initializes the performance-counter subsystem, once per process.
    /// </summary>
    /// <remarks>
    /// The subsystem initializes on this type's first PDH call: the query below measured
    /// ~1300 ms on a cold Windows host and ~0.15 ms on every call after it. The cost is
    /// process-global, so it is paid through <see cref="Warmup"/> rather than per reader instance,
    /// and a host that measures stage timings should construct the reader while preparing (see
    /// <c>ControlledDubbingBenchmarkRunner.PrepareHostAsync</c>) so the stall is charged to host
    /// setup instead of to whichever stage happened to read first.
    /// </remarks>
    private static bool WarmCounterSubsystem()
    {
        try
        {
            _ = EnumerateProcessInstances();
        }
        catch (Exception exception)
        {
            // A warm-up failure is not the reading's verdict: reads retry and report their own
            // reason. Recording the cause keeps it visible in evidence.
            warmupFailure = exception.GetType().Name;
        }

        return true;
    }

    public string UnavailableReason =>
        warmupFailure is { } failure
            ? $"Windows performance counter initialization failed ({failure})."
            : $"Windows reported no {CounterSetName} counter instance for this process.";

    public long? ReadDedicatedGpuMemoryBytes()
    {
        try
        {
            return QueryDedicatedUsageBytes();
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            // A host without a usable pdh.dll degrades the reading instead of aborting the run. A
            // missing entry point is deliberately not caught here: pdh.dll always exports these,
            // so that would mean a mistyped import and must fail loudly rather than silently
            // disable the feature. The telemetry collector still declines it in production.
            return null;
        }
    }

    private static long? QueryDedicatedUsageBytes()
    {
        if (NativeMethods.PdhOpenQueryW(null, 0, out nint query) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            // The English API accepts this wildcard path on every display language and expands
            // instances when the array is formatted. This avoids both localized object names and
            // the stale instance list retained by PdhEnumObjectItemsW after warm-up.
            string path = $@"\{CounterSetName}(pid_{Environment.ProcessId}_*)\{DedicatedUsageCounterName}";
            if (NativeMethods.PdhAddEnglishCounterW(query, path, 0, out nint counter) != ErrorSuccess ||
                NativeMethods.PdhCollectQueryData(query) != ErrorSuccess)
            {
                return null;
            }

            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = NativeMethods.PdhGetFormattedCounterArrayW(
                counter, FormatLarge, ref bufferSize, ref itemCount, nint.Zero);
            if (status != MoreData || bufferSize == 0 || itemCount == 0)
            {
                return null;
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                status = NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, FormatLarge, ref bufferSize, ref itemCount, buffer);
                if (status != ErrorSuccess)
                {
                    return null;
                }

                long total = 0;
                int readings = 0;
                int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (uint index = 0; index < itemCount; index++)
                {
                    PdhFmtCounterValueItem item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                        buffer + (nint)(index * (uint)stride));
                    if (item.Value.Status is not (ValidDataStatus or NewDataStatus) || item.Value.LargeValue < 0)
                    {
                        continue;
                    }

                    total = checked(total + item.Value.LargeValue);
                    readings++;
                }

                return readings == 0 ? null : total;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            _ = NativeMethods.PdhCloseQuery(query);
        }
    }

    private static class NativeMethods
    {
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nint userData, out nint counter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCollectQueryData(nint query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterArrayW(
            nint counter, uint format, ref uint bufferSize, ref uint itemCount, nint buffer);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCloseQuery(nint query);
    }

    /// <summary>
    /// Mirrors PDH_FMT_COUNTERVALUE: a status word followed by an 8-byte union. With
    /// <see cref="FormatLarge"/> the union holds the LONGLONG member.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint Status;
        public long LargeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }
}
