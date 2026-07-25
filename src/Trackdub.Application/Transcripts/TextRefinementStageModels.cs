using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed record TextRefinementStageRequest(
    Guid ProjectId,
    IReadOnlyList<TextRefinementInputSegment> Segments,
    TextRefinementScope Scope = TextRefinementScope.Asr,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    string? PreferredModelAlias = null,
    bool RequirePreferredModelAlias = false,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record TextRefinementStageResult(
    StageRunRecord StageRun,
    TextRefinementScope Scope,
    IReadOnlyList<RefinedTextSegment> Segments);

public sealed record TranscriptSegmentTextProvenance(
    int SegmentIndex,
    TranscriptSegmentTextSource TextSource,
    bool Accepted,
    string OriginalText,
    string DisplayedText,
    IReadOnlyList<string> AppliedCorrections,
    string? RefinedText = null,
    Guid? RefinementStageRunId = null);

public sealed record TextRefinementProvenanceArtifactDocument(
    Guid TranscriptRevisionId,
    Guid? TextRefinementStageRunId,
    TextRefinementScope Scope,
    IReadOnlyList<TranscriptSegmentTextProvenance> Segments,
    DateTimeOffset GeneratedAtUtc);

public sealed record RawAsrTranscriptArtifact(
    Guid StageRunId,
    IReadOnlyList<RecognizedTranscriptSegment> Segments);

public static class TextRefinementSegmentResolution
{
    public static bool UseRefinementResults(TextRefinementStageResult? result) =>
        result is not null &&
        result.StageRun.Status is StageRunStatus.Completed or StageRunStatus.PartiallyCompleted;

    public static RefinedTextSegment? FindSegment(TextRefinementStageResult? result, int index) =>
        result?.Segments.FirstOrDefault(segment => segment.Index == index);

    public static string ResolveDisplayedText(
        RecognizedTranscriptSegment asrSegment,
        TextRefinementStageResult? result)
    {
        if (!UseRefinementResults(result))
        {
            return asrSegment.Text;
        }

        return FindSegment(result, asrSegment.Index)?.DisplayedText ?? asrSegment.Text;
    }

    public static Guid ResolveRevisionStageRunId(AsrStageResult asrResult, TextRefinementStageResult? result)
    {
        if (UseRefinementResults(result) && result!.Segments.Any(segment => segment.Accepted))
        {
            return result.StageRun.Id;
        }

        return asrResult.StageRun.Id;
    }

    public static string ResolveActiveTranscriptProvenance(TextRefinementStageResult? result) =>
        UseRefinementResults(result) && result!.Segments.Any(segment => segment.Accepted)
            ? "generated-asr-polished"
            : "generated-asr";

    public static IReadOnlyList<TranscriptSegmentTextProvenance> BuildProvenance(
        AsrStageResult asrResult,
        TextRefinementStageResult? result)
    {
        Guid? refinementRunId = result?.StageRun.Id;
        return asrResult.Segments
            .OrderBy(segment => segment.Index)
            .Select(asrSegment =>
            {
                RefinedTextSegment? refined = FindSegment(result, asrSegment.Index);
                if (refined is null || !UseRefinementResults(result))
                {
                    return new TranscriptSegmentTextProvenance(
                        asrSegment.Index,
                        TranscriptSegmentTextSource.RawAsr,
                        Accepted: false,
                        OriginalText: asrSegment.Text,
                        DisplayedText: asrSegment.Text,
                        AppliedCorrections: [TextRefinementCorrectionCodes.FallbackUnchanged],
                        RefinementStageRunId: refinementRunId);
                }

                return new TranscriptSegmentTextProvenance(
                    asrSegment.Index,
                    refined.Accepted ? TranscriptSegmentTextSource.PolishedAsr : TranscriptSegmentTextSource.RawAsr,
                    refined.Accepted,
                    refined.OriginalText,
                    refined.DisplayedText,
                    refined.AppliedCorrections,
                    refined.Accepted ? refined.RefinedText : null,
                    refinementRunId);
            })
            .ToArray();
    }
}
