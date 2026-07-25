using Trackdub.Contracts;
using Trackdub.Media.Loudness;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegLoudnessCommandBuilderTests
{
    [Fact]
    public void BuildFirstPassArguments_uses_loudnorm_json_analysis_filter()
    {
        IReadOnlyList<string> arguments = FfmpegLoudnessCommandBuilder.BuildFirstPassArguments(
            "input.wav",
            -23d);

        Assert.Contains("loudnorm=I=-23:TP=-1.5:LRA=11:print_format=json", arguments);
        Assert.Contains("-f", arguments);
        Assert.Contains("null", arguments);
    }

    [Fact]
    public void BuildSecondPassArguments_uses_measured_loudnorm_values()
    {
        var stats = new LoudnormStats(
            InputIntegratedLufs: -19.6d,
            InputTruePeak: -2.2d,
            InputLra: 6.8d,
            InputThreshold: -30.1d,
            OutputIntegratedLufs: -14.1d,
            TargetOffset: -0.4d);

        IReadOnlyList<string> arguments = FfmpegLoudnessCommandBuilder.BuildSecondPassArguments(
            "input.wav",
            "output.wav",
            -14d,
            stats);

        Assert.Contains(
            "loudnorm=I=-14:TP=-1.5:LRA=11:measured_I=-19.6:measured_TP=-2.2:measured_LRA=6.8:measured_thresh=-30.1:offset=-0.4:linear=true:print_format=json",
            arguments);
        Assert.Contains("pcm_s16le", arguments);
        Assert.Contains("48000", arguments);
    }

    [Fact]
    public void ParseStats_reads_ffmpeg_loudnorm_json_from_stderr()
    {
        const string standardError = """
            [Parsed_loudnorm_0 @ 000001] 
            {
                "input_i" : "-18.72",
                "input_tp" : "-1.48",
                "input_lra" : "5.20",
                "input_thresh" : "-28.80",
                "output_i" : "-13.94",
                "target_offset" : "-0.06"
            }
            """;

        LoudnormStats stats = FfmpegLoudnessCommandBuilder.ParseStats(standardError);

        Assert.Equal(-18.72d, stats.InputIntegratedLufs);
        Assert.Equal(-1.48d, stats.InputTruePeak);
        Assert.Equal(5.20d, stats.InputLra);
        Assert.Equal(-28.80d, stats.InputThreshold);
        Assert.Equal(-13.94d, stats.OutputIntegratedLufs);
        Assert.Equal(-0.06d, stats.TargetOffset);
    }

    [Fact]
    public void ParseStats_skips_non_loudnorm_json_blocks()
    {
        const string standardError = """
            [graph @ 000001] {"filter":"not-loudnorm"}
            [Parsed_loudnorm_0 @ 000002]
            {
                "input_i" : "-18.72",
                "input_tp" : "-1.48",
                "input_lra" : "5.20",
                "input_thresh" : "-28.80",
                "output_i" : "-13.94",
                "target_offset" : "-0.06"
            }
            [Parsed_other_0 @ 000003] {"ignored":true}
            """;

        LoudnormStats stats = FfmpegLoudnessCommandBuilder.ParseStats(standardError);

        Assert.Equal(-18.72d, stats.InputIntegratedLufs);
        Assert.Equal(-0.06d, stats.TargetOffset);
    }

    [Fact]
    public void ParseStats_rejects_missing_target_offset()
    {
        const string standardError = """
            {
                "input_i" : "-18.72",
                "input_tp" : "-1.48",
                "input_lra" : "5.20",
                "input_thresh" : "-28.80"
            }
            """;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            FfmpegLoudnessCommandBuilder.ParseStats(standardError));

        Assert.Contains("parseable JSON stats", exception.Message);
    }

    [Fact]
    public void BuildSecondPassFilter_replaces_loudnorm_infinity_values_with_finite_fallbacks()
    {
        const string standardError = """
            {
                "input_i" : "-inf",
                "input_tp" : "inf",
                "input_lra" : "0.00",
                "input_thresh" : "-70.00",
                "target_offset" : "-inf"
            }
            """;

        LoudnormStats stats = FfmpegLoudnessCommandBuilder.ParseStats(standardError);
        string filter = FfmpegLoudnessCommandBuilder.BuildSecondPassFilter(-14d, stats);

        Assert.Equal(double.NegativeInfinity, stats.InputIntegratedLufs);
        Assert.Equal(double.PositiveInfinity, stats.InputTruePeak);
        Assert.Equal(double.NegativeInfinity, stats.TargetOffset);
        Assert.Contains("measured_I=-14", filter);
        Assert.Contains("measured_TP=-1.5", filter);
        Assert.Contains("offset=0", filter);
        Assert.DoesNotContain("-inf", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("=inf", filter, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalizeAsync_rejects_output_path_that_matches_input_before_delete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-loudness-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string inputPath = Path.Combine(tempDirectory, "dub.wav");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3], TestContext.Current.CancellationToken);
        var normalizer = new FfmpegLoudnessNormalizer(new RecordingProcessRunner(), ffmpegPath: "ffmpeg");

        try
        {
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                normalizer.NormalizeAsync(
                    new LoudnessNormalizationRequest(inputPath, inputPath, -14d),
                    TestContext.Current.CancellationToken));

            Assert.Contains("must be different", exception.Message);
            Assert.True(File.Exists(inputPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_runs_first_pass_and_returns_integrated_loudness_without_output_file()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"trackdub-loudness-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string inputPath = Path.Combine(tempDirectory, "source.wav");
        string unexpectedOutputPath = Path.Combine(tempDirectory, "analysis-output.wav");
        string ffmpegPath = Path.Combine(tempDirectory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        await File.WriteAllBytesAsync(inputPath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(ffmpegPath, [], TestContext.Current.CancellationToken);
        var processRunner = new RecordingProcessRunner(
            new ProcessResult(0, string.Empty, """
                {
                    "input_i" : "-18.72",
                    "input_tp" : "-1.48",
                    "input_lra" : "5.20",
                    "input_thresh" : "-28.80",
                    "output_i" : "-13.94",
                    "target_offset" : "-0.06"
                }
                """));
        var normalizer = new FfmpegLoudnessNormalizer(processRunner, ffmpegPath);

        try
        {
            LoudnessAnalysisResult result = await normalizer.AnalyzeAsync(
                new LoudnessAnalysisRequest(inputPath),
                TestContext.Current.CancellationToken);

            IReadOnlyList<string> arguments = Assert.Single(processRunner.Calls).Arguments;
            Assert.Equal(-18.72d, result.IntegratedLufs);
            Assert.Contains("-f", arguments);
            Assert.Contains("null", arguments);
            Assert.DoesNotContain(unexpectedOutputPath, arguments);
            Assert.False(File.Exists(unexpectedOutputPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class RecordingProcessRunner(params ProcessResult[] results) : IProcessRunner
    {
        private readonly Queue<ProcessResult> results = new(results);
        private readonly List<ProcessRunCall> calls = [];

        public IReadOnlyList<ProcessRunCall> Calls => calls;

        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            calls.Add(new ProcessRunCall(executablePath, arguments));
            return Task.FromResult(results.TryDequeue(out ProcessResult? result)
                ? result
                : new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record ProcessRunCall(
        string ExecutablePath,
        IReadOnlyList<string> Arguments);
}
