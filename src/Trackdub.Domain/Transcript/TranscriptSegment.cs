namespace Trackdub.Domain.Transcript;

public sealed record TranscriptSegment
{
    public TranscriptSegment(
        Guid id,
        Guid transcriptRevisionId,
        int segmentIndex,
        double startSeconds,
        double endSeconds,
        string text,
        Guid? speakerId = null,
        string? detectedLanguage = null,
        IReadOnlyList<TranscriptWord>? words = null)
    {
        Id = id;
        TranscriptRevisionId = transcriptRevisionId;
        SegmentIndex = segmentIndex;
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        Text = text;
        SpeakerId = speakerId;
        DetectedLanguage = NormalizeLanguageCode(detectedLanguage);
        Words = NormalizeWords(words);
    }

    public Guid Id { get; init; }

    public Guid TranscriptRevisionId { get; init; }

    public int SegmentIndex { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Text { get; init; }

    public Guid? SpeakerId { get; init; }

    public string? DetectedLanguage { get; init; }

    public IReadOnlyList<TranscriptWord> Words { get; init; }

    public static TranscriptSegment Create(
        Guid transcriptRevisionId,
        int segmentIndex,
        double startSeconds,
        double endSeconds,
        string text,
        Guid? speakerId = null,
        string? detectedLanguage = null,
        IReadOnlyList<TranscriptWord>? words = null)
    {
        if (transcriptRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Transcript revision id is required.", nameof(transcriptRevisionId));
        }

        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex), "Segment index cannot be negative.");
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Segment start must be finite and non-negative.");
        }

        if (!double.IsFinite(endSeconds) || endSeconds < startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "Segment end must be finite and greater than or equal to the start.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Segment text is required.", nameof(text));
        }

        return new TranscriptSegment(
            Guid.NewGuid(),
            transcriptRevisionId,
            segmentIndex,
            startSeconds,
            endSeconds,
            text.Trim(),
            speakerId,
            NormalizeLanguageCode(detectedLanguage),
            words);
    }

    private static string? NormalizeLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().Replace('_', '-').ToLowerInvariant();

    private static IReadOnlyList<TranscriptWord> NormalizeWords(IReadOnlyList<TranscriptWord>? words) =>
        words is null || words.Count == 0
            ? []
            : words
                .OrderBy(static word => word.WordIndex)
                .Select((word, index) => TranscriptWord.Create(
                    index,
                    word.StartSeconds,
                    word.EndSeconds,
                    word.Text,
                    word.Confidence))
                .ToArray();
}
