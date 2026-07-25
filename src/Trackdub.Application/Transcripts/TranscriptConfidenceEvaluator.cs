using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Transcripts;

public enum TranscriptConfidenceLevel
{
    None,
    High,
    Medium,
    Low
}

public sealed record TranscriptConfidenceAssessment(
    TranscriptConfidenceLevel Level,
    double? MinimumConfidence,
    double Threshold,
    bool ReviewRecommended)
{
    public bool HasConfidenceData => MinimumConfidence is not null;
}

public sealed record TranscriptConfidenceSummary(
    int TotalSegments,
    int SegmentsWithConfidence,
    int ReviewRecommendedCount,
    double Threshold);

public static class TranscriptConfidenceEvaluator
{
    private const double HighConfidenceFloor = 0.90d;

    public static TranscriptConfidenceAssessment Assess(
        TranscriptSegment segment,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(segment);
        double normalizedThreshold = NormalizeThreshold(threshold);
        double[] confidences = segment.Words
            .Select(static word => word.Confidence)
            .OfType<double>()
            .ToArray();
        if (confidences.Length == 0)
        {
            return Assess((double?)null, normalizedThreshold);
        }

        return Assess(confidences.Min(), normalizedThreshold);
    }

    public static TranscriptConfidenceAssessment Assess(
        double? minimumConfidence,
        double threshold)
    {
        double normalizedThreshold = NormalizeThreshold(threshold);
        if (minimumConfidence is not double value)
        {
            return new TranscriptConfidenceAssessment(
                TranscriptConfidenceLevel.None,
                MinimumConfidence: null,
                normalizedThreshold,
                ReviewRecommended: false);
        }

        TranscriptConfidenceLevel level = value < normalizedThreshold
            ? TranscriptConfidenceLevel.Low
            : value < HighConfidenceFloor
                ? TranscriptConfidenceLevel.Medium
                : TranscriptConfidenceLevel.High;

        return new TranscriptConfidenceAssessment(
            level,
            value,
            normalizedThreshold,
            ReviewRecommended: level is TranscriptConfidenceLevel.Low);
    }

    public static TranscriptConfidenceSummary Summarize(
        IReadOnlyList<TranscriptSegment> segments,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(segments);
        double normalizedThreshold = NormalizeThreshold(threshold);
        TranscriptConfidenceAssessment[] assessments = segments
            .Select(segment => Assess(segment, normalizedThreshold))
            .ToArray();
        return new TranscriptConfidenceSummary(
            segments.Count,
            assessments.Count(static assessment => assessment.HasConfidenceData),
            assessments.Count(static assessment => assessment.ReviewRecommended),
            normalizedThreshold);
    }

    public static double NormalizeThreshold(double threshold) =>
        double.IsFinite(threshold) && threshold is >= 0d and <= 1d
            ? threshold
            : 0.75d;
}
