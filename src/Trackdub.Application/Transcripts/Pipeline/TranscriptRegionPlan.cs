using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Transcripts.Pipeline;

public sealed record TranscriptRegionPlan(
    IReadOnlyList<SpeechRegion> Regions,
    IReadOnlyDictionary<int, Guid> SpeakerIdsBySegmentIndex);
