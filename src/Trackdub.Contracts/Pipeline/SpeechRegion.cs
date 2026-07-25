namespace Trackdub.Contracts.Pipeline;

public sealed record SpeechRegion
{
    public SpeechRegion(int index, double startSeconds, double endSeconds)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Region index cannot be negative.");
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Region start must be finite and non-negative.");
        }

        if (!double.IsFinite(endSeconds) || endSeconds < startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "Region end must be finite and greater than or equal to the start.");
        }

        Index = index;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
    }

    public int Index { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }
}
