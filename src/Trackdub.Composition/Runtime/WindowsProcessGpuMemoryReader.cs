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
    private const uint FormatLarge = 0x0000_0400;

    /// <summary>PERF_DETAIL_WIZARD: enumerate every instance, not just the default subset.</summary>
    private const uint DetailWizard = 400;

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
    /// The subsystem initializes on this type's first PDH call: the enumeration below measured
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
        string[] instances = EnumerateProcessInstances();
        if (instances.Length == 0)
        {
            return null;
        }

        if (NativeMethods.PdhOpenQueryW(null, 0, out nint query) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            var counters = new List<nint>(instances.Length);
            foreach (string instance in instances)
            {
                // PdhAddEnglishCounter resolves the English name on any OS display language, so a
                // localized Windows install still finds the counter set.
                string path = $@"\{CounterSetName}({instance})\{DedicatedUsageCounterName}";
                if (NativeMethods.PdhAddEnglishCounterW(query, path, 0, out nint counter) == ErrorSuccess)
                {
                    counters.Add(counter);
                }
            }

            // These are instantaneous counters, so one collection is enough; only rate counters
            // need a second sample to produce a value.
            if (counters.Count == 0 || NativeMethods.PdhCollectQueryData(query) != ErrorSuccess)
            {
                return null;
            }

            long total = 0;
            int readings = 0;
            foreach (nint counter in counters)
            {
                if (NativeMethods.PdhGetFormattedCounterValue(counter, FormatLarge, out _, out PdhFmtCounterValue value) != ErrorSuccess)
                {
                    continue;
                }

                if (value.Status is not (ValidDataStatus or NewDataStatus) || value.LargeValue < 0)
                {
                    continue;
                }

                total = checked(total + value.LargeValue);
                readings++;
            }

            return readings == 0 ? null : total;
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

    /// <summary>
    /// Instance names of the counter set that belong to the current process. Returns an empty
    /// array when the counter set does not exist, which is the normal no-GPU case.
    /// </summary>
    private static string[] EnumerateProcessInstances()
    {
        uint counterLength = 0;
        uint instanceLength = 0;
        uint status = NativeMethods.PdhEnumObjectItemsW(
            null, null, CounterSetName,
            nint.Zero, ref counterLength,
            nint.Zero, ref instanceLength,
            DetailWizard, 0);
        if (status != MoreData || counterLength == 0 || instanceLength == 0)
        {
            return [];
        }

        // Both lists must have room for their reported sizes. Handing PDH a null buffer while its
        // length is the nonzero size from the sizing call fails with PDH_INVALID_ARGUMENT, so the
        // second call sizes both lists rather than repeating the null-buffer probe for one of them.
        nint counterBuffer = Marshal.AllocHGlobal(checked((int)counterLength * sizeof(char)));
        nint instanceBuffer = Marshal.AllocHGlobal(checked((int)instanceLength * sizeof(char)));
        try
        {
            uint counterCapacity = counterLength;
            uint instanceCapacity = instanceLength;
            status = NativeMethods.PdhEnumObjectItemsW(
                null, null, CounterSetName,
                counterBuffer, ref counterCapacity,
                instanceBuffer, ref instanceCapacity,
                DetailWizard, 0);
            if (status != ErrorSuccess)
            {
                return [];
            }

            string prefix = $"pid_{Environment.ProcessId}_";
            var matches = new List<string>();
            foreach (string instance in ReadMultiString(instanceBuffer, instanceCapacity))
            {
                // The trailing underscore keeps pid_123_ from matching pid_1234_....
                if (instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(instance);
                }
            }

            return matches.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(instanceBuffer);
            Marshal.FreeHGlobal(counterBuffer);
        }
    }

    /// <summary>Walks a double-null-terminated MULTI_SZ buffer produced by PDH.</summary>
    private static IEnumerable<string> ReadMultiString(nint buffer, uint characterCount)
    {
        int offset = 0;
        int end = checked((int)characterCount);
        while (offset < end)
        {
            string? value = Marshal.PtrToStringUni(buffer + (offset * sizeof(char)));
            if (string.IsNullOrEmpty(value))
            {
                yield break;
            }

            yield return value;
            offset += value.Length + 1;
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

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

        [DllImport("pdh.dll", EntryPoint = "PdhEnumObjectItemsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint PdhEnumObjectItemsW(
            string? dataSource, string? machineName, string objectName,
            nint counterList, ref uint counterListLength,
            nint instanceList, ref uint instanceListLength,
            uint detailLevel, uint flags);

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
}
