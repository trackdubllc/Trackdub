namespace Trackdub.Domain.Pipeline;

/// <summary>
/// Retry budget for transient-failure stages. Defaults mirror the legacy hardcoded
/// <c>StageRunHelper.TransientFailureRetryOptions</c> shape (3 attempts, 50ms doubling
/// backoff clamped at a safe upper bound) so behavior is unchanged on existing callers.
/// Hoisted from Application to Domain so future per-stage tuning can be threaded
/// (per-EP bundle, per-model weight, etc.) without forking the StageRunHelper chokepoint.
/// See <c>docs/internal/pipeline-readiness-spec.md</c> §4.4 + §11.9.
/// </summary>
public sealed record StageRetryBudget
{
    private int _maxBackoffMs = MaxBackoffMsDefault;

    public int MaxAttempts { get; }
    public int BaseBackoffMs { get; }
    public int MaxBackoffMs
    {
        get => _maxBackoffMs;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            _maxBackoffMs = value;
        }
    }

    /// <summary>
    /// Sentinel default for <see cref="MaxBackoffMs"/>. 50 * 2^10 = 51200 matches the legacy
    /// <c>Math.Min(attempt - 1, 10)</c> shift cap on the pre-extraction doubling-backoff math.
    /// </summary>
    public const int MaxBackoffMsDefault = 51_200;

    /// <summary>
    /// Hard upper bound on <see cref="MaxAttempts"/>. Anything beyond this is rejected at
    /// construction so misconfiguration cannot produce runaway retry storms.
    /// </summary>
    public const int MaxAttemptsHardCap = 10;

    public StageRetryBudget(int maxAttempts, int baseBackoffMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, MaxAttemptsHardCap);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseBackoffMs, 0);
        MaxAttempts = maxAttempts;
        BaseBackoffMs = baseBackoffMs;
    }

    /// <summary>
    /// Default budget that mirrors the legacy hardcoded 3-attempt / 50ms doubling-backoff
    /// behavior locked at the StageRunHelper chokepoint before the §11.9 extraction.
    /// </summary>
    public static StageRetryBudget Default { get; } = new(maxAttempts: 3, baseBackoffMs: 50);

    /// <summary>
    /// Backoff delay before the next retry attempt. Doubles each attempt up to a cap at
    /// <see cref="MaxBackoffMs"/>. Mirrors the legacy
    /// <c>BaseBackoffMs * (1 &lt;&lt; Math.Min(attempt - 1, 10))</c> shape:
    /// attempts 1..3 yield 50ms / 100ms / 200ms; further doublings clamp at
    /// <see cref="MaxBackoffMs"/>.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        long calculated = (long)BaseBackoffMs * (1L << Math.Min(attempt - 1, 10));
        long capped = Math.Min(calculated, MaxBackoffMs);
        return TimeSpan.FromMilliseconds(capped);
    }
}
