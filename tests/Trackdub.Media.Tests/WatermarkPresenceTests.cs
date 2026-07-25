using FsCheck;
using Trackdub.Media.Muxing;

namespace Trackdub.Media.Tests;

// Feature: licensing-and-tier-gates, Property 8: Watermark presence iff Free tier
// **Validates: Requirements 4.1, 4.2, 4.3**
public sealed class WatermarkPresenceTests
{
    private static readonly Arbitrary<int> OutputHeightArb =
        Arb.From(Gen.Choose(0, 4320));

    [Fact]
    public void Watermark_drawtext_present_iff_requires_watermark()
    {
        var property = Prop.ForAll(
            Arb.Default.Bool(),
            OutputHeightArb,
            (requiresWatermark, outputHeight) =>
            {
                var filterChain = FfmpegMuxCommandBuilder.BuildVideoFilterChain(
                    subtitlePath: null,
                    requiresWatermark: requiresWatermark,
                    outputHeight: outputHeight);

                bool containsWatermark = filterChain.Contains("Made with Trackdub", StringComparison.Ordinal);
                return containsWatermark == requiresWatermark;
            });

        property.QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Watermark_drawtext_present_with_subtitles_iff_requires_watermark()
    {
        var property = Prop.ForAll(
            Arb.Default.Bool(),
            OutputHeightArb,
            (requiresWatermark, outputHeight) =>
            {
                var filterChain = FfmpegMuxCommandBuilder.BuildVideoFilterChain(
                    subtitlePath: "captions.ass",
                    requiresWatermark: requiresWatermark,
                    outputHeight: outputHeight);

                bool containsWatermark = filterChain.Contains("Made with Trackdub", StringComparison.Ordinal);
                return containsWatermark == requiresWatermark;
            });

        property.QuickCheckThrowOnFailure();
    }
}
