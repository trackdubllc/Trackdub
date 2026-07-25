using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Mixing;

namespace Trackdub.Domain.Tests;

public sealed class MixPlanDomainTests
{
    [Fact]
    public void MixPlan_normalizes_channel_count_when_constructed()
    {
        MixPlan plan = CreateMixPlan(OutputChannelCount: 6);

        Assert.Equal(2, plan.OutputChannelCount);
    }

    [Fact]
    public void MixPlan_normalizes_channel_count_when_set_with_initializer()
    {
        MixPlan plan = CreateMixPlan() with { OutputChannelCount = 6 };

        Assert.Equal(2, plan.OutputChannelCount);
    }

    [Fact]
    public void MixPlan_uses_current_source_path_when_original_mix_path_is_blank()
    {
        MixPlan plan = CreateMixPlan() with
        {
            SourceAudioRelativePath = "artifacts/source-reopened.wav",
            OriginalMixAudioRelativePath = " "
        };

        Assert.Equal("artifacts/source-reopened.wav", plan.OriginalMixAudioRelativePath);
    }

    [Fact]
    public void MixPlan_treats_explicit_source_original_mix_path_as_equal_to_default()
    {
        MixPlan defaultPlan = CreateMixPlan();
        MixPlan explicitPlan = defaultPlan with { OriginalMixAudioRelativePath = "artifacts/source.wav" };

        Assert.Equal(defaultPlan, explicitPlan);
        Assert.Equal(defaultPlan.GetHashCode(), explicitPlan.GetHashCode());
    }

    [Fact]
    public void MixPlan_treats_source_original_mix_path_case_variants_as_equal_to_default()
    {
        MixPlan defaultPlan = CreateMixPlan();
        MixPlan explicitPlan = defaultPlan with { OriginalMixAudioRelativePath = "ARTIFACTS/SOURCE.WAV" };

        Assert.Equal(defaultPlan, explicitPlan);
        Assert.Equal(defaultPlan.GetHashCode(), explicitPlan.GetHashCode());
    }

    [Fact]
    public void MixPlan_treats_trimmed_backslash_source_original_mix_path_as_equal_to_default()
    {
        MixPlan defaultPlan = CreateMixPlan();
        MixPlan explicitPlan = defaultPlan with { OriginalMixAudioRelativePath = " artifacts\\source.wav " };

        Assert.Equal(defaultPlan, explicitPlan);
        Assert.Equal(defaultPlan.GetHashCode(), explicitPlan.GetHashCode());
    }

    [Fact]
    public void MixPlan_recanonicalizes_original_mix_path_when_source_path_matches_later_initializer()
    {
        MixPlan baseline = CreateMixPlan();
        MixPlan plan = baseline with
        {
            OriginalMixAudioRelativePath = "artifacts/source-reopened.wav",
            SourceAudioRelativePath = "artifacts/source-reopened.wav"
        };

        MixPlan expected = baseline with
        {
            SourceAudioRelativePath = "artifacts/source-reopened.wav"
        };

        Assert.Equal(expected, plan);
        Assert.Equal("artifacts/source-reopened.wav", plan.OriginalMixAudioRelativePath);
    }

    private static MixPlan CreateMixPlan(string? OriginalMixAudioRelativePath = null, int OutputChannelCount = 1) =>
        new(
            Guid.NewGuid(),
            MediaAssetId: null,
            ArtifactKind.NormalizedAudio,
            "artifacts/source.wav",
            SourceGainDb: 0d,
            DubbedSpeechGainDb: 0d,
            DuckingGainDb: -12d,
            DuckingLeadSeconds: 0.05d,
            DuckingTailSeconds: 0.18d,
            DateTimeOffset.Parse("2026-05-07T00:00:00+00:00"),
            SpeechClips: [],
            DuckingRegions: [],
            Warnings: [],
            OriginalMixAudioRelativePath: OriginalMixAudioRelativePath,
            OutputChannelCount: OutputChannelCount);
}
