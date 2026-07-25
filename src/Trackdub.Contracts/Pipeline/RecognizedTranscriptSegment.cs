namespace Trackdub.Contracts.Pipeline;

public sealed record RecognizedTranscriptSegment
{
    public RecognizedTranscriptSegment(
        int Index,
        double StartSeconds,
        double EndSeconds,
        string Text,
        string? DetectedLanguage = null,
        IReadOnlyList<RecognizedTranscriptWord>? Words = null)
    {
        this.Index = Index;
        this.StartSeconds = StartSeconds;
        this.EndSeconds = EndSeconds;
        this.Text = Text;
        this.DetectedLanguage = DetectedLanguage;
        this.Words = NormalizeWords(Words);
    }

    public int Index { get; init; }

    public double StartSeconds { get; init; }

    public double EndSeconds { get; init; }

    public string Text { get; init; }

    public string? DetectedLanguage { get; init; }

    public IReadOnlyList<RecognizedTranscriptWord> Words { get; init; }

    private static IReadOnlyList<RecognizedTranscriptWord> NormalizeWords(IReadOnlyList<RecognizedTranscriptWord>? words) =>
        words is null || words.Count == 0
            ? []
            : words
                .OrderBy(static word => word.WordIndex)
                .Select((word, index) => new RecognizedTranscriptWord(
                    index,
                    word.StartSeconds,
                    word.EndSeconds,
                    word.Text,
                    word.Confidence))
                .ToArray();
}
