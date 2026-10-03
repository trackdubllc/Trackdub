using Trackdub.Contracts;
using Trackdub.Media.Muxing;

namespace Trackdub.Media.Tests;

public sealed class WatermarkFontfileTests
{
    [Fact]
    public void BuildWatermarkFilter_includes_fontfile_when_font_path_is_provided()
    {
        string filter = FfmpegMuxCommandBuilder.BuildWatermarkFilter(
            1080,
            @"C:\Windows\Fonts\arial.ttf");

        Assert.Contains("fontfile='", filter, StringComparison.Ordinal);
        Assert.Contains(@"C\:\\Windows\\Fonts\\arial.ttf", filter, StringComparison.Ordinal);
        Assert.Contains("Made with Trackdub", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWatermarkFilter_omits_fontfile_when_font_path_is_missing()
    {
        string filter = FfmpegMuxCommandBuilder.BuildWatermarkFilter(1080);

        Assert.DoesNotContain("fontfile", filter, StringComparison.Ordinal);
        Assert.Contains("Made with Trackdub", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildVideoFilterChain_threads_font_path_into_watermark_only()
    {
        string chain = FfmpegMuxCommandBuilder.BuildVideoFilterChain(
            subtitlePath: "captions.ass",
            requiresWatermark: true,
            outputHeight: 1080,
            watermarkFontPath: "/usr/share/fonts/DejaVuSans.ttf");

        Assert.Contains("fontfile='/usr/share/fonts/DejaVuSans.ttf'", chain, StringComparison.Ordinal);
        Assert.Contains("subtitles='", chain, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArguments_emits_fontfile_for_watermarked_plan()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mp4",
            ExportOutputContainer.Mp4,
            BurnInSubtitlePath: null,
            SourceLanguage: "en",
            TargetLanguage: "fr",
            RequiresWatermark: true,
            OutputHeight: 1080,
            WatermarkFontPath: @"C:\Windows\Fonts\arial.ttf");

        string joined = string.Join("|", FfmpegMuxCommandBuilder.BuildArguments(plan));

        Assert.Contains("drawtext=fontfile='", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void WatermarkFontResolver_returns_null_or_an_existing_absolute_path()
    {
        string? resolved = WatermarkFontResolver.ResolveFontPath();

        Assert.True(
            resolved is null || (Path.IsPathRooted(resolved) && File.Exists(resolved)),
            $"Resolver returned an unusable path: {resolved}");
    }
}
