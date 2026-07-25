namespace Trackdub.Domain.Translation;

public sealed record TranslatedWord
{
    public TranslatedWord(
        int wordIndex,
        double startSeconds,
        double endSeconds,
        string text)
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
    }

    public int WordIndex { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Text { get; init; }

    public static TranslatedWord Create(
        int wordIndex,
        double startSeconds,
        double endSeconds,
        string text) =>
        new(wordIndex, startSeconds, endSeconds, text);
}
