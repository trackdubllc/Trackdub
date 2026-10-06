using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Environment-driven configuration for the process-wide
/// <see cref="InferenceSessionPool.Shared"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// Budgeted memory admission is <em>on</em> by default (hard admission): a cache miss that
/// would exceed the applicable budget waits for budget (evicting idle entries first) and a
/// single model that exceeds the whole budget fails fast. Setting
/// <c>TRACKDUB_SESSION_ADMISSION</c> to <c>0</c>/<c>false</c>/<c>off</c>/<c>disabled</c> is an
/// explicit diagnostic opt-out that restores the historical behaviour where a miss past
/// <c>maxSessions</c> creates a short-lived ephemeral session.
/// </para>
/// <list type="bullet">
///   <item><description><c>TRACKDUB_SESSION_ADMISSION</c> — <c>0</c>/<c>false</c>/<c>off</c>/<c>disabled</c> to disable; anything else (including unset) keeps admission enabled.</description></item>
///   <item><description><c>TRACKDUB_SESSION_VRAM_BUDGET_MB</c> — positive integer accelerator budget in MB, applied per device. Invalid values fall back to <see cref="InferenceSessionPool.DefaultMemoryBudgetMb"/>.</description></item>
///   <item><description><c>TRACKDUB_SESSION_RAM_BUDGET_MB</c> — positive integer host RAM budget in MB, shared by CPU/DNNL and OpenVINO CPU-proxy sessions. Invalid values fall back to <see cref="InferenceSessionPool.DefaultHostMemoryBudgetMb"/>.</description></item>
///   <item><description><c>TRACKDUB_SESSION_MAX_SESSIONS</c> — positive integer capacity (count mode). Invalid or overflowing values fall back to the pool default.</description></item>
///   <item><description><c>TRACKDUB_SESSION_PROCESS_GPU_ADMISSION</c> — <c>0</c>/<c>false</c>/<c>off</c>/<c>disabled</c> to stop accelerator admission from accounting for this process's own dedicated GPU usage; anything else (including unset) keeps it on.</description></item>
/// </list>
/// <para>
/// The environment values are captured once, when the shared pool is first resolved, so
/// changing the environment afterwards has no effect on the running process.
/// </para>
/// <para>
/// The one non-environment input is <see cref="ProcessGpuMemoryReader"/>, the host's
/// process-isolated dedicated GPU reading. A host registers it with
/// <see cref="UseProcessGpuMemoryReader"/>, which the shared pool consults on every accelerator
/// admission decision; until it is registered the pool accounts for its own reservations only.
/// </para>
/// </remarks>
public static class SharedPoolOptions
{
    /// <summary>Opt-out switch: only an explicit negative disables budgeted session admission.</summary>
    public const string AdmissionVariable = "TRACKDUB_SESSION_ADMISSION";

    /// <summary>Optional accelerator (VRAM) budget override, in MB.</summary>
    public const string BudgetMbVariable = "TRACKDUB_SESSION_VRAM_BUDGET_MB";

    /// <summary>Optional host RAM budget override, in MB.</summary>
    public const string HostBudgetMbVariable = "TRACKDUB_SESSION_RAM_BUDGET_MB";

    /// <summary>Optional session capacity override.</summary>
    public const string MaxSessionsVariable = "TRACKDUB_SESSION_MAX_SESSIONS";

    /// <summary>
    /// Opt-out switch for process-isolated GPU admission: only an explicit negative stops the
    /// pool from accounting for this process's own dedicated GPU usage.
    /// </summary>
    public const string ProcessGpuAdmissionVariable = "TRACKDUB_SESSION_PROCESS_GPU_ADMISSION";

    /// <summary>Whether budgeted admission is enabled for the shared pool.</summary>
    public static bool EnableMemoryAdmission { get; } = ReadAdmissionFlag(AdmissionVariable);

    /// <summary>Accelerator (VRAM) admission budget in MB, per device. Defaults to the VRAM-scaled pool default when unset.</summary>
    public static long MemoryBudgetMb { get; } =
        ReadPositiveInt64(BudgetMbVariable) ?? InferenceSessionPool.DefaultMemoryBudgetMb;

    /// <summary>Host RAM admission budget in MB, shared by CPU/DNNL and OpenVINO CPU-proxy sessions. Defaults to the RAM-scaled pool default when unset.</summary>
    public static long HostMemoryBudgetMb { get; } =
        ReadPositiveInt64(HostBudgetMbVariable) ?? InferenceSessionPool.DefaultHostMemoryBudgetMb;

    /// <summary>Maximum pooled session capacity; the pool default when unset or invalid.</summary>
    public static int MaxSessions { get; } =
        ReadPositiveInt32(MaxSessionsVariable) ?? InferenceSessionPool.DefaultMaxSessions;

    /// <summary>
    /// Whether accelerator admission also accounts for this process's real dedicated GPU
    /// usage (see <see cref="ProcessGpuMemoryReader"/>). On by default: the reading only ever
    /// tightens admission, and it is the only signal that can see GPU memory this process holds
    /// outside the pool's own reservations (driver contexts, arenas, non-pooled consumers).
    /// </summary>
    public static bool EnableProcessGpuAdmission { get; } = ReadAdmissionFlag(ProcessGpuAdmissionVariable);

    private static IProcessGpuMemoryReader? processGpuMemoryReader;

    /// <summary>
    /// The host's process-isolated dedicated GPU reading, or <see langword="null"/> when no host
    /// has registered one. Read on every accelerator admission decision, so registering a reader
    /// after the shared pool was first resolved still takes effect.
    /// </summary>
    public static IProcessGpuMemoryReader? ProcessGpuMemoryReader =>
        Volatile.Read(ref processGpuMemoryReader);

    /// <summary>
    /// Registers <paramref name="reader"/> as the shared pool's process-GPU observation, or
    /// clears it with <see langword="null"/>. Composition calls this as the host's reader is
    /// created (see <c>HeadlessCompositionRoot</c>); the reader is consulted only while
    /// <see cref="EnableProcessGpuAdmission"/> is on, and a reader that cannot report a reading
    /// leaves admission behaviour exactly as it was.
    /// </summary>
    public static void UseProcessGpuMemoryReader(IProcessGpuMemoryReader? reader) =>
        Volatile.Write(ref processGpuMemoryReader, reader);

    internal static bool ReadAdmissionFlag(string variable)
        => ParseAdmissionFlag(Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Parses an admission switch value. The guarded behaviour is the safe default: only an
    /// explicit negative token (<c>0</c>/<c>false</c>/<c>off</c>/<c>disabled</c>,
    /// case-insensitive) disables it; unset, blank, and unrecognised values keep it enabled so a
    /// typo cannot silently drop the memory guard.
    /// </summary>
    internal static bool ParseAdmissionFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "off" or "disabled" => false,
            _ => true,
        };
    }

    internal static long? ReadPositiveInt64(string variable)
        => ParsePositiveInt64(Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Parses a positive integer override, returning <see langword="null"/> when the value is
    /// absent or not a positive integer so the caller keeps its default.
    /// </summary>
    internal static long? ParsePositiveInt64(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!long.TryParse(raw.Trim(), out long parsed) || parsed < 1)
        {
            return null;
        }

        return parsed;
    }

    internal static int? ReadPositiveInt32(string variable)
        => ParsePositiveInt32(Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Parses a positive 32-bit integer override, returning <see langword="null"/> when the
    /// value is absent, not a positive integer, or exceeds <see cref="int.MaxValue"/> so
    /// oversized environment values fall back instead of faulting static initialisation.
    /// </summary>
    internal static int? ParsePositiveInt32(string? raw)
    {
        long? parsed = ParsePositiveInt64(raw);
        return parsed is > int.MaxValue ? null : (int?)parsed;
    }
}
