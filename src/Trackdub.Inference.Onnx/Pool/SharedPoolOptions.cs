namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Environment-driven configuration for the process-wide
/// <see cref="InferenceSessionPool.Shared"/> instance.
/// </summary>
/// <remarks>
/// <para>
/// Budgeted memory admission is <em>off</em> by default, which preserves the historical
/// behaviour: a cache miss beyond <c>maxSessions</c> creates a short-lived ephemeral session
/// instead of waiting. Turning it on makes a miss wait for budget (evicting idle entries
/// first) and fails fast when a single model exceeds the whole budget.
/// </para>
/// <para>
/// Admission is opt-in because its wait path blocks until budget frees, so an
/// under-sized budget can stall a run rather than merely over-allocating. Operators
/// should measure a representative pipeline on their own hardware first, then enable it
/// with a budget sized to the target GPU.
/// </para>
/// <list type="bullet">
///   <item><description><c>TRACKDUB_SESSION_ADMISSION</c> — <c>1</c>/<c>true</c>/<c>on</c>/<c>enabled</c> to enable; anything else (including unset) leaves it off.</description></item>
///   <item><description><c>TRACKDUB_SESSION_VRAM_BUDGET_MB</c> — positive integer budget in MB. Ignored when admission is off. Invalid values fall back to <see cref="InferenceSessionPool.DefaultMemoryBudgetMb"/>.</description></item>
///   <item><description><c>TRACKDUB_SESSION_MAX_SESSIONS</c> — positive integer capacity. Invalid values fall back to the pool default.</description></item>
/// </list>
/// <para>
/// The value is captured once, when the shared pool is first resolved, so changing the
/// environment afterwards has no effect on the running process.
/// </para>
/// </remarks>
public static class SharedPoolOptions
{
    /// <summary>Opt-in switch enabling budgeted session admission.</summary>
    public const string AdmissionVariable = "TRACKDUB_SESSION_ADMISSION";

    /// <summary>Optional VRAM budget override, in MB.</summary>
    public const string BudgetMbVariable = "TRACKDUB_SESSION_VRAM_BUDGET_MB";

    /// <summary>Optional session capacity override.</summary>
    public const string MaxSessionsVariable = "TRACKDUB_SESSION_MAX_SESSIONS";

    /// <summary>When budgeted admission is enabled for the shared pool.</summary>
    public static bool EnableMemoryAdmission { get; } = ReadAdmissionFlag(AdmissionVariable);

    /// <summary>VRAM admission budget in MB; meaningful only when <see cref="EnableMemoryAdmission"/> is set.</summary>
    public static long MemoryBudgetMb { get; } =
        ReadPositiveInt64(BudgetMbVariable) ?? InferenceSessionPool.DefaultMemoryBudgetMb;

    /// <summary>Maximum pooled session capacity; the pool default when unset or invalid.</summary>
    public static int MaxSessions { get; } =
        (int)(ReadPositiveInt64(MaxSessionsVariable) ?? InferenceSessionPool.DefaultMaxSessions);

    internal static bool ReadAdmissionFlag(string variable)
        => ParseAdmissionFlag(Environment.GetEnvironmentVariable(variable));

    /// <summary>
    /// Parses an admission switch value. Only an explicit affirmative token enables
    /// admission; every other value — including a typo — leaves it disabled. Matching is
    /// case-insensitive so an operator writing <c>TRUE</c> or <c>On</c> is not silently
    /// ignored.
    /// </summary>
    internal static bool ParseAdmissionFlag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Trim() switch
        {
            "1" => true,
            var value => value.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("enabled", StringComparison.OrdinalIgnoreCase),
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
}