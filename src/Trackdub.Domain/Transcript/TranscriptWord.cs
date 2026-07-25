namespace Trackdub.Domain.Transcript;

public sealed record TranscriptWord
{
    public TranscriptWord(
        int wordIndex,
        double startSeconds,
        double endSeconds,
        string text,
        double? confidence = null)
    {
        if (wordIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordIndex), "Word index cannot be negative.");
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Word start must be finite and non-negative.");
        }

        if (!double.IsFinite(endSeconds) || endSeconds < startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "Word end must be finite and greater than or equal to the start.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Word text is required.", nameof(text));
        }

        WordIndex = wordIndex;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        Text = text.Trim();
        Confidence = NormalizeConfidence(confidence);
    }

    public int WordIndex { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Text { get; init; }

    public double? Confidence { get; init; }

    public static TranscriptWord Create(
        int wordIndex,
        double startSeconds,
        double endSeconds,
        string text,
        double? confidence = null) =>
        new(wordIndex, startSeconds, endSeconds, text, confidence);

    private static double? NormalizeConfidence(double? confidence)
    {
        if (confidence is not double value || !double.IsFinite(value))
        {
            return null;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
