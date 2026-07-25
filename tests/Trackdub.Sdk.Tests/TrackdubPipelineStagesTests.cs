using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubPipelineStagesTests
{
    [Fact]
    public void RequiresSourceMedia_LipSynthesis_ReturnsTrue()
    {
        Assert.True(TrackdubPipelineStages.RequiresSourceMedia(StageNames.LipSynthesis));
    }

    [Theory]
    [InlineData(StageNames.Translation)]
    [InlineData(StageNames.Tts)]
    [InlineData(StageNames.Export)]
    public void RequiresSourceMedia_ArtifactStages_ReturnFalse(string stageName)
    {
        Assert.False(TrackdubPipelineStages.RequiresSourceMedia(stageName));
    }

    [Theory]
    [InlineData(StageNames.Separation, false)]
    [InlineData(StageNames.Vad, false)]
    [InlineData(StageNames.Diarization, false)]
    [InlineData(StageNames.Asr, false)]
    [InlineData(StageNames.Translation, true)]
    [InlineData(StageNames.Tts, true)]
    [InlineData(StageNames.LipSync, false)]
    [InlineData(StageNames.Export, false)]
    [InlineData(StageNames.LipSynthesis, false)]
    public void RequiresTargetLanguage_ClassifiesEveryKnownStage(string stageName, bool expected)
    {
        Assert.Equal(expected, TrackdubPipelineStages.RequiresTargetLanguage(stageName));
    }

    [Fact]
    public void RequiresTargetLanguage_TheoryCoversCompleteStageCatalog()
    {
        string[] classifiedStages =
        [
            StageNames.Separation,
            StageNames.Vad,
            StageNames.Diarization,
            StageNames.Asr,
            StageNames.Translation,
            StageNames.Tts,
            StageNames.LipSync,
            StageNames.Export,
            StageNames.LipSynthesis,
        ];

        Assert.Equal(
            Trackdub.Application.Dubbing.DubbingPipelineStages.ExtendedStageOrder,
            classifiedStages);
    }
}
