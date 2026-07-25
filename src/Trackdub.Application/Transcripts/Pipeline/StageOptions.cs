namespace Trackdub.Application.Transcripts.Pipeline;

/// <summary>
/// Per-stage execution options threaded through the pipeline builder.
/// All properties are optional; omitted properties inherit the pipeline default.
/// </summary>
public sealed record StageOptions
{
    /// <summary>
    /// Maximum wall-clock time allowed for a single stage execution.
    /// When the budget is exhausted the stage's <see cref="CancellationToken"/> is cancelled,
    /// the stage run is recorded as <c>Canceled</c> (with a timeout reason), and the
    /// pipeline propagates the <see cref="OperationCanceledException"/> to the caller without
    /// writing an unhandled-exception degradation record.
    /// <c>null</c> (the default) means no timeout is enforced.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Must be positive if non-null.</exception>
    public TimeSpan? Timeout
    {
        get => _timeout;
        init
        {
            if (value is not null && value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Timeout),
                    value,
                    "Stage timeout must be a positive duration.");
            }

            _timeout = value;
        }
    }

    private readonly TimeSpan? _timeout;

    /// <summary>Default options with no timeout enforced.</summary>
    public static readonly StageOptions Default = new();
}
