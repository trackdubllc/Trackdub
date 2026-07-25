using Trackdub.Application.Transcripts;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.Transcript;

namespace Trackdub.Application.LipSynthesis;

public static class LipSynthesisSegmentUiStateBuilder
{
    public static IReadOnlyList<LipSynthesisSegmentUiState>? Build(
        IReadOnlyList<LipSynthesisSegment> segments,
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<SpeakerTurn> speakerTurns)
    {
        if (segments.Count == 0 || transcriptSegments.Count == 0 || speakerTurns.Count == 0)
        {
            return null;
        }

        Dictionary<Guid, LipSynthesisSegment> lipByTurnId = segments
            .GroupBy(static segment => segment.SegmentId)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static segment => segment.CreatedAtUtc).First());

        var results = new List<LipSynthesisSegmentUiState>();
        foreach (TranscriptSegment segment in transcriptSegments)
        {
            LipSynthesisSegment? selected = null;
            foreach (SpeakerTurn turn in speakerTurns)
            {
                if (!Overlaps(segment.StartSeconds, segment.EndSeconds, turn.StartSeconds, turn.EndSeconds))
                {
                    continue;
                }

                if (!lipByTurnId.TryGetValue(turn.Id, out LipSynthesisSegment? candidate))
                {
                    continue;
                }

                selected = selected is null ? candidate : PickMoreInformative(selected, candidate);
            }

            if (selected is null || selected.Status is LipSynthesisSegmentStatus.NotRun)
            {
                continue;
            }

            results.Add(new LipSynthesisSegmentUiState(
                segment.SegmentIndex,
                selected.Status,
                selected.FaceConfidence,
                selected.SkipReason,
                selected.FailureReason,
                selected.ProviderId,
                selected.ModelId,
                selected.UsedExperimentalProvider));
        }

        return results.Count == 0 ? null : results;
    }

    private static bool Overlaps(double segStart, double segEnd, double turnStart, double turnEnd) =>
        segStart < turnEnd && turnStart < segEnd;

    private static LipSynthesisSegment PickMoreInformative(LipSynthesisSegment current, LipSynthesisSegment candidate) =>
        Rank(candidate.Status) > Rank(current.Status) ? candidate : current;

    private static int Rank(LipSynthesisSegmentStatus status) =>
        status switch
        {
            LipSynthesisSegmentStatus.Synthesized => 100,
            LipSynthesisSegmentStatus.Failed => 80,
            LipSynthesisSegmentStatus.SkippedLowConfidence => 60,
            LipSynthesisSegmentStatus.SkippedNoFace => 50,
            LipSynthesisSegmentStatus.SkippedNonFrontal => 50,
            LipSynthesisSegmentStatus.SkippedOccluded => 50,
            LipSynthesisSegmentStatus.SkippedUnstableCrop => 50,
            LipSynthesisSegmentStatus.SkippedLicenseGate => 40,
            LipSynthesisSegmentStatus.SkippedExperimentalGate => 40,
            LipSynthesisSegmentStatus.SkippedRuntimeUnavailable => 40,
            _ => 0
        };
}
