using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Muxing;

namespace Trackdub.Media.Tests;

public sealed class FfmpegVideoRecompositionCommandBuilderTests
{
    [Fact]
    public void BuildFilterGraph_single_turn_offsets_and_overlays_on_timeline()
    {
        var plan = new ResolvedVideoRecompositionPlan(
            @"C:\source.mp4",
            [new ResolvedRecomposedTurn(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3.25), @"C:\patch.mp4")]);

        string filter = FfmpegVideoRecompositionCommandBuilder.BuildFilterGraph(plan.PatchedTurns);

        Assert.Equal(
            "[1:v]setpts=PTS+1.5/TB[p0];[0:v][p0]overlay=0:0:enable='between(t\\,1.5\\,3.25)'[vout]",
            filter);
    }

    [Fact]
    public void BuildFilterGraph_multiple_turns_chain_overlays_in_order()
    {
        var plan = new ResolvedVideoRecompositionPlan(
            @"C:\source.mp4",
            [
                new ResolvedRecomposedTurn(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), @"C:\a.mp4"),
                new ResolvedRecomposedTurn(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), @"C:\b.mp4")
            ]);

        string filter = FfmpegVideoRecompositionCommandBuilder.BuildFilterGraph(plan.PatchedTurns);

        Assert.Equal(
            "[1:v]setpts=PTS+1/TB[p0];[2:v]setpts=PTS+4/TB[p1];" +
            "[0:v][p0]overlay=0:0:enable='between(t\\,1\\,2)'[v0];" +
            "[v0][p1]overlay=0:0:enable='between(t\\,4\\,6)'[vout]",
            filter);
    }

    [Fact]
    public void BuildArguments_maps_recomposed_video_without_audio()
    {
        var plan = new ResolvedVideoRecompositionPlan(
            @"C:\source.mp4",
            [new ResolvedRecomposedTurn(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.5), @"C:\patch.mp4")]);

        IReadOnlyList<string> arguments = FfmpegVideoRecompositionCommandBuilder.BuildArguments(plan, @"C:\out.mp4");
        string joined = string.Join('|', arguments);

        Assert.Contains("-filter_complex|", joined, StringComparison.Ordinal);
        Assert.Contains("-map|[vout]|", joined, StringComparison.Ordinal);
        Assert.Contains("|-an|", joined, StringComparison.Ordinal);
        Assert.Contains("|-c:v|libx264|", joined, StringComparison.Ordinal);
        Assert.EndsWith(@"C:\out.mp4", joined, StringComparison.Ordinal);
    }
}
