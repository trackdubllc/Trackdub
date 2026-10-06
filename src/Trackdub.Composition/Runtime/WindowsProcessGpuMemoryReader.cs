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
/// The reading uses one wildcard counter (<c>\GPU Process Memory(*)\Dedicated Usage</c>) added
/// through the language-neutral English API, with per-instance values retrieved through
/// <c>PdhGetFormattedCounterArrayW</c> and filtered to this process's <c>pid_&lt;pid&gt;_</c>
/// prefix. Enumerating instances one by one would need the localized performance-object name
/// (English fails on localized Windows installs) and would rely on PDH's cached instance list,
/// which can predate this process's first GPU work; the wildcard array reflects the instances
/// present at each collection instead.
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

    private const uint ErrorSuccess = 0;
    private const uint MoreData = 0x8000_07D2;
    private const uint ValidDataStatus = 0;
    private const uint NewDataStatus = 1;

    private static readonly Lazy<bool> Warmup = new(
        WarmCounterSubsystem, LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile string? warmupFailure;
    private volatile string? lastReadFailure;

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
            _ = QueryObservation(out _);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A warm-up failure is not the reading's verdict: reads retry and report their own
            // reason. Recording the cause keeps it visible in evidence.
            warmupFailure = exception.GetType().Name;
        }

        return true;
    }

    public string UnavailableReason => lastReadFailure
        ?? (warmupFailure is { } failure
            ? $"Windows performance counter initialization failed ({failure})."
            : "No current Windows process GPU measurement is available.");

    public long? ReadDedicatedGpuMemoryBytes() => ReadObservation()?.TotalBytes;

    public (long? TotalBytes, IReadOnlyDictionary<long, long>? ByAdapterLuid) ReadDedicatedGpuMemory()
    {
        GpuMemoryObservation? observation = ReadObservation();
        return (observation?.TotalBytes, observation?.ByAdapterLuid);
    }

    public IReadOnlyDictionary<long, long>? ReadDedicatedGpuMemoryBytesByAdapterLuid() =>
        ReadObservation()?.ByAdapterLuid;

    private GpuMemoryObservation? ReadObservation()
    {
        try
        {
            GpuMemoryObservation? observation = QueryObservation(out string? failure);
            lastReadFailure = failure;
            return observation;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            lastReadFailure = $"Windows GPU memory probe could not load pdh.dll ({exception.GetType().Name}).";
            return null;
        }
    }

    /// <summary>
    /// Parses the adapter LUID out of a GPU Process Memory instance name
    /// (<c>pid_&lt;pid&gt;_luid_&lt;high&gt;_&lt;low&gt;_phys_&lt;n&gt;</c>, components in hex
    /// or decimal) into the <c>long</c> form DXGI reports (<c>(High &lt;&lt; 32) | Low</c>).
    /// </summary>
    internal static bool TryParseAdapterLuid(string instanceName, out long adapterLuid)
    {
        adapterLuid = 0;
        // pid_<pid>_luid_<high>_<low>_phys_<n>
        string[] parts = instanceName.Split('_');
        if (parts.Length != 7
            || !parts[0].Equals("pid", StringComparison.OrdinalIgnoreCase)
            || !parts[2].Equals("luid", StringComparison.OrdinalIgnoreCase)
            || !parts[5].Equals("phys", StringComparison.OrdinalIgnoreCase)
            || !TryParseLuidPart(parts[3], out long high)
            || !TryParseLuidPart(parts[4], out long low))
        {
            return false;
        }

        adapterLuid = checked((high << 32) | (uint)low);
        return true;
    }

    private static bool TryParseLuidPart(string text, out long value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value))
        {
            return true;
        }

        return long.TryParse(text, out value);
    }

    private static GpuMemoryObservation? QueryObservation(out string? failure)
    {
        failure = null;
        uint openStatus = NativeMethods.PdhOpenQueryW(null, 0, out nint query);
        if (openStatus != ErrorSuccess)
        {
            failure = $"PDH query open failed (0x{openStatus:X8}).";
            return null;
        }

        try
        {
            // One wildcard counter through the language-neutral English API: the object name
            // needs no localization, and the per-instance array below reflects the instances
            // present at this collection, so a process that starts GPU work after the reader
            // was constructed is still observed.
            string path = $@"\{CounterSetName}(*)\{DedicatedUsageCounterName}";
            uint addStatus = NativeMethods.PdhAddEnglishCounterW(query, path, 0, out nint counter);
            if (addStatus != ErrorSuccess)
            {
                // The counter set itself is absent (no GPU, a server SKU without GPU
                // counters, or a driver that does not publish them).
                failure = $"PDH GPU counter registration failed (0x{addStatus:X8}).";
                return null;
            }

            // These are instantaneous counters, so one collection is enough; only rate counters
            // need a second sample to produce a value.
            uint collectStatus = NativeMethods.PdhCollectQueryData(query);
            if (collectStatus != ErrorSuccess)
            {
                failure = $"PDH GPU counter collection failed (0x{collectStatus:X8}).";
                return null;
            }

            uint bufferSize = 0;
            uint status = NativeMethods.PdhGetFormattedCounterArrayW(
                counter, FormatLarge, ref bufferSize, out uint itemCount, nint.Zero);
            if (status != MoreData && status != ErrorSuccess)
            {
                failure = $"PDH GPU counter array sizing failed (0x{status:X8}).";
                return null;
            }

            if (itemCount == 0 || bufferSize == 0)
            {
                // The counter set accepted the wildcard but publishes no instances at all:
                // no process holds dedicated GPU memory, so neither does this one.
                return new GpuMemoryObservation(0, new Dictionary<long, long>());
            }

            nint buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
            try
            {
                status = NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, FormatLarge, ref bufferSize, out itemCount, buffer);
                if (status != ErrorSuccess)
                {
                    failure = $"PDH GPU counter formatting failed (0x{status:X8}).";
                    return null;
                }

                // The trailing underscore keeps pid_123_ from matching pid_1234_....
                string prefix = $"pid_{Environment.ProcessId}_";
                int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                long total = 0;
                int readings = 0;
                bool matched = false;
                bool invalid = false;
                bool unattributed = false;
                var byAdapter = new Dictionary<long, long>();
                for (uint i = 0; i < itemCount; i++)
                {
                    nint itemPtr = buffer + (int)(i * (uint)itemSize);
                    PdhFmtCounterValueItem item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPtr);
                    string? name = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrEmpty(name)
                        || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matched = true;
                    if (item.Value.Status is not (ValidDataStatus or NewDataStatus)
                        || item.Value.LargeValue < 0)
                    {
                        invalid = true;
                        continue;
                    }

                    total = checked(total + item.Value.LargeValue);
                    readings++;
                    if (!TryParseAdapterLuid(name, out long adapterLuid))
                    {
                        // An instance that cannot be attributed to an adapter breaks the
                        // per-adapter sum invariant, so the breakdown is withheld while the
                        // process total still stands.
                        unattributed = true;
                        continue;
                    }

                    byAdapter[adapterLuid] = checked(
                        byAdapter.TryGetValue(adapterLuid, out long attributed)
                            ? attributed + item.Value.LargeValue
                            : item.Value.LargeValue);
                }

                // The counter set exists but publishes no instance for this process: it holds
                // no dedicated GPU memory right now, which is a genuine zero rather than a gap.
                // (A missing counter set already returned null above.)
                if (!matched)
                {
                    return new GpuMemoryObservation(0, new Dictionary<long, long>());
                }

                // A partial footprint would under-report the process's real usage and let
                // admission exceed the budget, so any unreadable instance fails the reading
                // instead of silently contributing nothing.
                if (invalid || readings == 0)
                {
                    failure = "A process GPU counter instance reported invalid data; the partial footprint was rejected.";
                    return null;
                }

                return new GpuMemoryObservation(total, unattributed ? null : byAdapter);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (OverflowException)
        {
            failure = "PDH GPU counter values or buffer size overflowed the supported range.";
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

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhGetFormattedCounterArrayW(
            nint counter, uint format, ref uint bufferSize, out uint bufferCount, nint itemBuffer);

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

    /// <summary>
    /// Mirrors PDH_FMT_COUNTERVALUE_ITEM_W: the instance name plus its formatted value.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public PdhFmtCounterValue Value;
    }
}
