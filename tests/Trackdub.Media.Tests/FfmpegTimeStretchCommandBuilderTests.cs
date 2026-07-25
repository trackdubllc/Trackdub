using Trackdub.Domain.Tts;
using Trackdub.Media.Stretch;

namespace Trackdub.Media.Tests;

public sealed class FfmpegTimeStretchCommandBuilderTests
{
    [Fact]
    public void BuildFilterPlan_uses_single_atempo_inside_native_range()
    {
        TimeStretchFilterPlan plan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
            1.15d,
            enableRubberband: false,
            rubberbandThreshold: 0.15d,
            rubberbandAvailable: false);

        Assert.Equal("atempo=1.15", plan.Filter);
        Assert.Equal(TtsStretchEngine.Atempo, plan.Engine);
        Assert.False(plan.UsedFallback);
    }

    [Fact]
    public void BuildFilterPlan_chains_two_atempo_filters_outside_native_range()
    {
        TimeStretchFilterPlan plan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
            3.0d,
            enableRubberband: false,
            rubberbandThreshold: 0.15d,
            rubberbandAvailable: false);

        Assert.Equal("atempo=1.732050807569,atempo=1.732050807569", plan.Filter);
        Assert.Equal(TtsStretchEngine.Atempo, plan.Engine);
    }

    [Fact]
    public void BuildFilterPlan_uses_rubberband_when_enabled_available_and_threshold_exceeded()
    {
        TimeStretchFilterPlan plan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
            1.20d,
            enableRubberband: true,
            rubberbandThreshold: 0.15d,
            rubberbandAvailable: true);

        Assert.Equal("rubberband=tempo=1.2", plan.Filter);
        Assert.Equal(TtsStretchEngine.Rubberband, plan.Engine);
    }

    [Fact]
    public void BuildFilterPlan_falls_back_to_atempo_when_rubberband_unavailable()
    {
        TimeStretchFilterPlan plan = FfmpegTimeStretchCommandBuilder.BuildFilterPlan(
            1.20d,
            enableRubberband: true,
            rubberbandThreshold: 0.15d,
            rubberbandAvailable: false);

        Assert.Equal("atempo=1.2", plan.Filter);
        Assert.Equal(TtsStretchEngine.Atempo, plan.Engine);
        Assert.True(plan.UsedFallback);
    }
}
