using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.Transcript;
using Xunit;

namespace Trackdub.Application.Tests;

public sealed class LipSynthesisSegmentUiStateBuilderTests
{
    [Fact]
    public void Build_maps_overlapping_speaker_turn_to_transcript_segment()
    {
        Guid turnId = Guid.NewGuid();
        var segments = new[]
        {
            new LipSynthesisSegment(
                turnId,
                LipSynthesisSegmentStatus.Synthesized,
                "spk-1",
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(3),
                0.9,
                "clips/patch.mp4",
                null,
                null,
                "latentsync",
                "ByteDance/LatentSync-1.6",
                true,
                DateTimeOffset.UtcNow)
        };
        var transcriptSegments = new[]
        {
            TranscriptSegment.Create(Guid.NewGuid(), 0, 1.5, 2.5, "hello", Guid.NewGuid())
        };
        var speakerTurns = new[]
        {
            new SpeakerTurn(turnId, Guid.NewGuid(), Guid.NewGuid(), 1.0, 3.0)
        };

        IReadOnlyList<LipSynthesisSegmentUiState>? uiStates = LipSynthesisSegmentUiStateBuilder.Build(
            segments,
            transcriptSegments,
            speakerTurns);

        LipSynthesisSegmentUiState uiState = Assert.Single(uiStates!);
        Assert.Equal(0, uiState.SegmentIndex);
        Assert.Equal(LipSynthesisSegmentStatus.Synthesized, uiState.Status);
        Assert.True(uiState.UsedExperimentalProvider);
    }
}
