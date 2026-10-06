using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Inference.Onnx.Runtime;

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Reads this process's dedicated GPU memory from the Windows "GPU Process Memory" performance
/// counter set, both as a process total and attributed per graphics adapter.
/// </summary>
/// <remarks>
/// <para>
/// Windows publishes that counter set per process: each instance is named
/// <c>pid_&lt;pid&gt;_luid_&lt;high&gt;_&lt;low&gt;_phys_&lt;n&gt;</c>, so its "Dedicated Usage"
/// counter is exactly the process-isolated allocation <see cref="IAvailableVramReader"/> cannot
/// attribute. A process with instances on several adapters (or on several physical partitions of
/// a linked display adapter) reports one instance each: the total is their sum, and the
/// per-adapter reading groups them by the LUID embedded in the instance name, which
/// <see cref="WindowsDeviceEnumerator.QueryAdapterIndexByLuid"/> maps to a device index.
/// </para>
/// <para>
/// One wildcard counter (<c>pid_&lt;pid&gt;_*</c>) is added through <c>PdhAddEnglishCounterW</c>
/// and read with <c>PdhGetFormattedCounterArrayW</c>. Both design choices are load-bearing: the
/// English path is locale-independent, so no localized performance-object name has to be
/// resolved before enumeration can succeed; and the wildcard expands at <em>collection</em> time,
/// so instances created after this reader was constructed — a late adapter, a partition that
/// appears on first use — are discovered by the next read instead of being missed forever.
/// </para>
/// <para>
/// PDH is called through <c>pdh.dll</c> directly rather than through a managed package so
/// composition keeps its existing zero-extra-dependency posture, matching the hand-written DXGI
/// P/Invoke in the inference layer. A host without the counter set (no GPU, a server SKU without
/// GPU counters, or a driver that does not publish them) reports an explicit unavailable result,
/// and each failure path records its own reason so evidence never attributes a probe failure to
/// a missing counter instance.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessGpuMemoryReader : IProcessGpuMemoryReader
{
    private const string CounterSetName = "GPU Process Memory";
    private const string DedicatedUsageCounterName = "Dedicated Usage";

    /// <summary>PDH_FMT_LARGE: return counters as 64-bit values rather than doubles.</summary>
    private const uint FormatLarge = 0x0000_0100;

    private const uint ErrorSuccess = 0;

    /// <summary>PDH_MORE_DATA: the caller's buffer was too small and now holds the required size.</summary>
    private const uint MoreData = 0x8000_07D2;

    /// <summary>PDH_NO_DATA: the query matched no counter instance for this process.</summary>
    private const uint NoData = 0x8000_07D5;

    /// <summary>PDH_CSTATUS_NO_OBJECT: the counter set does not exist on this host.</summary>
    private const uint CounterObjectMissing = 0xC000_0BB8;

    private const uint ValidDataStatus = 0;
    private const uint NewDataStatus = 1;

    /// <summary>
    /// Native size of <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>: an 8-byte name pointer, then a
    /// 16-byte <c>PDH_FMT_COUNTERVALUE</c> whose 8-byte union starts at offset 8.
    /// </summary>
    private const int CounterValueItemSize = 24;

    private static readonly Lazy<bool> Warmup = new(
        WarmCounterSubsystem, LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile string? warmupFailure;

    /// <summary>
    /// Reason recorded by the most recent failed read, distinct per failure path so benchmark
    /// evidence says *why* the reading is unavailable instead of the generic no-instance message.
    /// </summary>
    private volatile string? lastFailure;

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
    /// setup instead of to whichever stage happened to read first. A GPU-idle process at startup
    /// simply finds no instance — that is a normal read result, not a warm-up failure, so only an
    /// exception is recorded here and reads keep reporting their own reason.
    /// </remarks>
    private static bool WarmCounterSubsystem()
    {
        try
        {
            _ = QueryUsage(out _);
        }
        catch (Exception exception) when (IsExpectedWarmupFailure(exception))
        {
            warmupFailure = exception.GetType().Name;
        }

        return true;
    }

    /// <summary>
    /// Operational failures a warm-up must degrade rather than propagate: an exception escaping
    /// here would be cached by <see cref="Warmup"/> and rethrown by every later reader
    /// construction instead of letting each read retry.
    /// </summary>
    private static bool IsExpectedWarmupFailure(Exception exception) =>
        exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
            or InvalidOperationException or UnauthorizedAccessException or COMException or OverflowException;

    public string UnavailableReason =>
        lastFailure
            ?? (warmupFailure is { } failure
                ? $"Windows performance counter initialization failed ({failure})."
                : $"Windows reported no {CounterSetName} counter instance for this process.");

    public long? ReadDedicatedGpuMemoryBytes()
    {
        try
        {
            UsageReading? reading = QueryUsage(out string? failure);
            lastFailure = failure;
            if (reading is null)
            {
                return null;
            }

            // The historic contract for the process-wide reading: a process that publishes no
            // counter instance at all is "cannot report", and callers keep treating it that way.
            return reading.InstanceCount == 0 ? null : reading.TotalBytes;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or OverflowException)
        {
            lastFailure = $"Windows performance counter query failed ({exception.GetType().Name}).";
            return null;
        }
    }

    public IReadOnlyDictionary<int, long>? ReadDedicatedGpuMemoryBytesByAdapter()
    {
        try
        {
            UsageReading? reading = QueryUsage(out string? failure);
            lastFailure = failure;
            if (reading is null)
            {
                return null;
            }

            if (!reading.FullyAttributed)
            {
                // The total above is still complete, but an instance name this build could not
                // parse has no LUID to map; decline per-adapter attribution so the caller falls
                // back to the aggregate rather than silently dropping that instance's bytes.
                return null;
            }

            var bytesByAdapter = new Dictionary<int, long>(reading.BytesByAdapterLuid.Count);
            foreach (KeyValuePair<long, long> pair in reading.BytesByAdapterLuid)
            {
#if WINDOWS
                int? deviceIndex = WindowsDeviceEnumerator.QueryAdapterIndexByLuid(pair.Key);
#else
                // The device enumerator that owns the LUID mapping is only compiled for Windows.
                int? deviceIndex = null;
#endif
                if (deviceIndex is null)
                {
                    // The LUID is not among the host's usable adapters, so its bytes cannot be
                    // attributed to a device index: fall back to the aggregate reading.
                    return null;
                }

                bytesByAdapter[deviceIndex.Value] =
                    bytesByAdapter.GetValueOrDefault(deviceIndex.Value) + pair.Value;
            }

            return bytesByAdapter;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or OverflowException)
        {
            lastFailure = $"Windows performance counter query failed ({exception.GetType().Name}).";
            return null;
        }
    }

    /// <summary>
    /// Collects one sample of every <c>pid_&lt;pid&gt;_*</c> instance and groups it by the
    /// adapter LUID embedded in the instance name. Returns <see langword="null"/> when the
    /// reading is unavailable; <paramref name="failure"/> then carries the distinct reason for
    /// that specific path.
    /// </summary>
    private static UsageReading? QueryUsage(out string? failure)
    {
        failure = null;

        // The wildcard expands when the query is collected, so instances created after an earlier
        // read (or after warm-up) are discovered instead of being invisible for the process's
        // lifetime; PdhAddEnglishCounterW resolves the English path on any OS display language,
        // so a localized Windows install still finds the counter set.
        string path = $@"\{CounterSetName}(pid_{Environment.ProcessId}_*)\{DedicatedUsageCounterName}";

        uint status = NativeMethods.PdhOpenQueryW(null, 0, out nint query);
        if (status != ErrorSuccess)
        {
            failure = $"Opening a Windows performance-counter query failed with PDH status 0x{status:X8}.";
            return null;
        }

        try
        {
            status = NativeMethods.PdhAddEnglishCounterW(query, path, 0, out nint counter);
            if (status != ErrorSuccess)
            {
                failure = status == CounterObjectMissing
                    ? $"Windows does not publish a '{CounterSetName}' counter set on this host."
                    : $"Adding the '{CounterSetName}' counter failed with PDH status 0x{status:X8}.";
                return null;
            }

            // These are instantaneous counters, so one collection is enough; only rate counters
            // need a second sample to produce a value.
            status = NativeMethods.PdhCollectQueryData(query);
            if (status == NoData)
            {
                // No matching instance: this process currently holds no dedicated GPU memory.
                return new UsageReading();
            }

            if (status != ErrorSuccess)
            {
                failure = $"Collecting '{CounterSetName}' samples failed with PDH status 0x{status:X8}.";
                return null;
            }

            return ReadFormattedArray(counter, out failure);
        }
        finally
        {
            _ = NativeMethods.PdhCloseQuery(query);
        }
    }

    /// <summary>
    /// Formats the collected sample into an array of per-instance values. The buffer is
    /// caller-allocated (PDH reports its required size through PDH_MORE_DATA) and is sized to a
    /// single retry so an instance set that grows between the sizing call and the read cannot
    /// produce a partial result.
    /// </summary>
    private static UsageReading? ReadFormattedArray(nint counter, out string? failure)
    {
        failure = null;
        nint buffer = nint.Zero;
        try
        {
            uint bufferLength = 0;
            uint itemCount = 0;
            uint status = NativeMethods.PdhGetFormattedCounterArrayW(
                counter, FormatLarge, ref bufferLength, ref itemCount, nint.Zero);
            if (status == NoData)
            {
                return new UsageReading();
            }

            if (status is not (MoreData or ErrorSuccess) || bufferLength == 0)
            {
                failure = $"Reading the '{CounterSetName}' counter array failed with PDH status 0x{status:X8}.";
                return null;
            }

            for (int attempt = 0; ; attempt++)
            {
                buffer = Marshal.AllocHGlobal(checked((int)bufferLength));
                uint capacity = bufferLength;
                status = NativeMethods.PdhGetFormattedCounterArrayW(
                    counter, FormatLarge, ref capacity, ref itemCount, buffer);
                if (status == ErrorSuccess)
                {
                    break;
                }

                Marshal.FreeHGlobal(buffer);
                buffer = nint.Zero;
                bufferLength = capacity;
                if (status != MoreData || attempt >= 1)
                {
                    failure = $"Reading the '{CounterSetName}' counter array failed with PDH status 0x{status:X8}.";
                    return null;
                }
            }

            return SumInstances(buffer, itemCount, out failure);
        }
        catch (OverflowException)
        {
            failure = "The dedicated GPU memory reading exceeded a 64-bit counter.";
            return null;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>
    /// Sums every instance's value, failing closed on the first invalid sample: a subtotal would
    /// undercount exactly the footprint pool admission and <c>--max-gpu-bytes</c> exist to see.
    /// </summary>
    private static UsageReading? SumInstances(nint buffer, uint itemCount, out string? failure)
    {
        failure = null;
        var reading = new UsageReading();
        long total = 0;
        for (uint i = 0; i < itemCount; i++)
        {
            var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                buffer + checked((int)i * CounterValueItemSize));
            if (item.Status is not (ValidDataStatus or NewDataStatus) || item.LargeValue < 0)
            {
                failure =
                    $"The '{CounterSetName}' counter reported an invalid sample for one of this " +
                    $"process's instances (status 0x{item.Status:X8}).";
                return null;
            }

            long? adapterLuid = TryParseAdapterLuid(Marshal.PtrToStringUni(item.Name) ?? string.Empty);
            if (adapterLuid is null)
            {
                reading.FullyAttributed = false;
            }
            else
            {
                reading.BytesByAdapterLuid[adapterLuid.Value] =
                    reading.BytesByAdapterLuid.GetValueOrDefault(adapterLuid.Value) + item.LargeValue;
            }

            total = checked(total + item.LargeValue);
        }

        reading.TotalBytes = total;
        reading.InstanceCount = checked((int)itemCount);
        return reading;
    }

    /// <summary>
    /// Extracts the adapter LUID from an instance name of the form
    /// <c>pid_&lt;pid&gt;_luid_0x&lt;high8&gt;_0x&lt;low8&gt;_phys_&lt;n&gt;</c>. The high part is
    /// written first, so the packed value matches the 64-bit LUID the DXGI device enumerator
    /// compares against. Returns <see langword="null"/> when the name is in an unexpected shape.
    /// </summary>
    private static long? TryParseAdapterLuid(string instance)
    {
        int marker = instance.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        ReadOnlySpan<char> highPart = instance.AsSpan(marker + "luid_".Length);
        if (!TryReadFixedHex(highPart, out long high, out int consumed))
        {
            return null;
        }

        ReadOnlySpan<char> lowPart = highPart.Slice(consumed);
        if (!lowPart.StartsWith("_0x", StringComparison.OrdinalIgnoreCase) ||
            !TryReadFixedHex(lowPart.Slice(1), out long low, out _))
        {
            return null;
        }

        return (high << 32) | (uint)low;
    }

    /// <summary>Reads one <c>0x</c>-prefixed hex field of at most 8 digits.</summary>
    private static bool TryReadFixedHex(ReadOnlySpan<char> text, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (text.Length < 3 || text[0] != '0' || text[1] is not ('x' or 'X'))
        {
            return false;
        }

        int index = 2;
        for (; index < text.Length && index < 10; index++)
        {
            int digit = text[index] switch
            {
                >= '0' and <= '9' => text[index] - '0',
                >= 'a' and <= 'f' => text[index] - 'a' + 10,
                >= 'A' and <= 'F' => text[index] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                break;
            }

            value = (value << 4) | (uint)digit;
        }

        if (index == 2)
        {
            return false;
        }

        consumed = index;
        return true;
    }

    /// <summary>One collected sample of this process's counter instances.</summary>
    private sealed class UsageReading
    {
        /// <summary>Dedicated bytes per adapter LUID, packed as the DXGI LUID's 64-bit layout.</summary>
        public Dictionary<long, long> BytesByAdapterLuid { get; } = new();

        /// <summary>Sum across every instance — the process-wide total.</summary>
        public long TotalBytes { get; set; }

        /// <summary>Number of instances summed.</summary>
        public int InstanceCount { get; set; }

        /// <summary>
        /// <see langword="false"/> when an instance name could not be parsed into an adapter
        /// LUID: the total is still complete, but per-adapter attribution must be declined.
        /// </summary>
        public bool FullyAttributed { get; set; } = true;
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
            nint counter, uint format, ref uint bufferLength, ref uint itemCount, nint itemBuffer);

        [DllImport("pdh.dll", ExactSpelling = true)]
        internal static extern uint PdhCloseQuery(nint query);
    }

    /// <summary>
    /// Mirrors <c>PDH_FMT_COUNTERVALUE_ITEM_W</c>. Explicit offsets rather than sequential
    /// layout: the native union is 8-byte aligned, and sequential layout would place it at
    /// offset 4 on a 32-bit runtime while PDH writes it at offset 8.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = CounterValueItemSize)]
    private struct PdhFmtCounterValueItem
    {
        /// <summary><c>LPWSTR szName</c> — the instance name, allocated by PDH.</summary>
        [FieldOffset(0)]
        public nint Name;

        /// <summary><c>PDH_FMT_COUNTERVALUE.Cstatus</c> — the sample's status word.</summary>
        [FieldOffset(8)]
        public uint Status;

        /// <summary><c>PDH_FMT_COUNTERVALUE</c>'s LONGLONG union member.</summary>
        [FieldOffset(16)]
        public long LargeValue;
    }
}
