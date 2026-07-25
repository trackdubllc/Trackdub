using Trackdub.Contracts;
using Trackdub.Media.Muxing;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegMuxCommandBuilderTests
{
    [Fact]
    public void BuildArguments_copies_video_stream_for_mp4_sidecar_export()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mp4",
            ExportOutputContainer.Mp4,
            BurnInSubtitlePath: null,
            SourceLanguage: "EN",
            TargetLanguage: "ES");

        IReadOnlyList<string> arguments = FfmpegMuxCommandBuilder.BuildArguments(plan);

        Assert.Contains("-c:v", arguments);
        Assert.Contains("copy", arguments);
        Assert.DoesNotContain("-vf", arguments);
        Assert.Contains("aac", arguments);
        Assert.Contains("192k", arguments);
        Assert.Contains("-shortest", arguments);
        Assert.Contains("DUBBED_BY=Trackdub", arguments);
        Assert.Contains("source_language=en", arguments);
        Assert.Contains("target_language=es", arguments);
        Assert.Contains("+faststart", arguments);
    }

    [Fact]
    public void BuildArguments_reencodes_video_when_subtitles_are_burned_in()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mkv",
            ExportOutputContainer.Mkv,
            BurnInSubtitlePath: "captions.ass",
            SourceLanguage: "en",
            TargetLanguage: "es");

        VideoEncodeProfile softwareProfile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Software,
            FfmpegVideoEncoderSnapshot.Empty);
        IReadOnlyList<string> arguments = FfmpegMuxCommandBuilder.BuildArguments(plan, softwareProfile);

        Assert.Contains("-vf", arguments);
        Assert.Contains("libx264", arguments);
        Assert.Contains("libopus", arguments);
        Assert.Contains("160k", arguments);
        Assert.DoesNotContain("+faststart", arguments);
        Assert.Contains(arguments, value => value.StartsWith("subtitles='", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArguments_escapes_subtitle_filter_delimiters_in_burn_in_path()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mp4",
            ExportOutputContainer.Mp4,
            BurnInSubtitlePath: Path.Combine("folder;bad", "captions[1].ass"),
            SourceLanguage: null,
            TargetLanguage: null);

        IReadOnlyList<string> arguments = FfmpegMuxCommandBuilder.BuildArguments(plan);

        string filter = Assert.Single(arguments, value => value.StartsWith("subtitles='", StringComparison.Ordinal));
        Assert.Contains(@"\;", filter);
        Assert.Contains(@"\[", filter);
        Assert.Contains(@"\]", filter);
    }

    [Fact]
    public void BuildArguments_copy_path_never_uses_hardware_encoders()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mp4",
            ExportOutputContainer.Mp4,
            BurnInSubtitlePath: null,
            SourceLanguage: null,
            TargetLanguage: null,
            VideoEncoder: VideoEncoderPreference.Nvenc);

        string joined = string.Join("|", FfmpegMuxCommandBuilder.BuildArguments(plan));

        Assert.DoesNotContain("h264_nvenc", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("h264_qsv", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("h264_amf", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("d3d11va", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildArguments_burn_in_with_nvenc_profile_uses_nvenc_encoder()
    {
        ExportPlan plan = new(
            "source.mp4",
            "dub.wav",
            "output.mp4",
            ExportOutputContainer.Mp4,
            BurnInSubtitlePath: "captions.ass",
            SourceLanguage: null,
            TargetLanguage: null,
            VideoEncoder: VideoEncoderPreference.Nvenc);

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Nvenc,
            SnapshotWithEncoders("h264_nvenc"));

        string joined = string.Join("|", FfmpegMuxCommandBuilder.BuildArguments(plan, profile));

        Assert.Contains("h264_nvenc", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderAsync_rejects_output_path_that_matches_source_before_delete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-mux-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string sourcePath = Path.Combine(tempDirectory, "source.mp4");
        string audioPath = Path.Combine(tempDirectory, "dub.wav");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(audioPath, [4, 5, 6], TestContext.Current.CancellationToken);
        var muxer = new FfmpegMuxer(new RecordingProcessRunner(), ffmpegPath: "ffmpeg");

        try
        {
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                muxer.RenderAsync(
                    new ExportPlan(
                        sourcePath,
                        audioPath,
                        sourcePath,
                        ExportOutputContainer.Mp4,
                        BurnInSubtitlePath: null,
                        SourceLanguage: null,
                        TargetLanguage: null),
                    TestContext.Current.CancellationToken));

            Assert.Contains("must be different", exception.Message);
            Assert.True(File.Exists(sourcePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RenderAsync_canceled_before_mux_throws_operation_canceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-mux-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string sourcePath = Path.Combine(tempDirectory, "source.mp4");
        string audioPath = Path.Combine(tempDirectory, "dub.wav");
        string outputPath = Path.Combine(tempDirectory, "out.mp4");
        string ffmpegPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3], CancellationToken.None);
        await File.WriteAllBytesAsync(audioPath, [4, 5, 6], CancellationToken.None);
        await File.WriteAllBytesAsync(ffmpegPath, [], CancellationToken.None);
        var muxer = new FfmpegMuxer(new CancelAwareProcessRunner(), ffmpegPath);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                muxer.RenderAsync(
                    new ExportPlan(
                        sourcePath,
                        audioPath,
                        outputPath,
                        ExportOutputContainer.Mp4,
                        BurnInSubtitlePath: null,
                        SourceLanguage: null,
                        TargetLanguage: null),
                    cts.Token));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static FfmpegVideoEncoderSnapshot SnapshotWithEncoders(params string[] encoders) =>
        new(
            new HashSet<string>(encoders, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

    private sealed class CancelAwareProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null) =>
            Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }
}
