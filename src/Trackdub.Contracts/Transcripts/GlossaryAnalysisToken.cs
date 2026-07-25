namespace Trackdub.Contracts.Transcripts;

public sealed record GlossaryAnalysisToken(
    int StartTextElementIndex,
    int TextElementLength,
    string SurfaceText,
    string NormalizedText,
    string? Lemma = null);
