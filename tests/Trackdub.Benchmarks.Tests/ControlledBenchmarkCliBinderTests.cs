using Trackdub.Benchmarks;

namespace Trackdub.Benchmarks.Tests;

public sealed class ControlledBenchmarkCliBinderTests
{
    [Fact]
    public void Flags_apply_to_shared_state()
    {
        var state = new ControlledBenchmarkCliOptions();
        using var error = new StringWriter();

        Assert.True(ControlledBenchmarkCliBinder.TryApplyFlag("--reuse-engine-cache", state));
        Assert.True(ControlledBenchmarkCliBinder.TryApplyFlag("--mock", state));
        Assert.True(ControlledBenchmarkCliBinder.TryApplyFlag("--dry-run", state));
        Assert.False(ControlledBenchmarkCliBinder.TryApplyFlag("--output", state));

        Assert.True(state.ReuseEngineCache);
        Assert.True(state.Mock);
        Assert.True(state.DryRun);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Shared_options_apply_identically_for_both_commands()
    {
        string[] sharedArgs =
        [
            "--output", "out",
            "--language", "fr",
            "--source-language", "en",
            "--mode", "warm-host",
            "--model-directory", "models",
            "--provider", "cpu",
            "--ffmpeg", "ffmpeg",
            "--ffprobe", "ffprobe",
            "--sha256", "abc",
            "--runs", "3",
            "--max-cpu-percent", "37.25",
            "--max-working-set-bytes", "4294967296",
            "--max-gpu-bytes", "4096",
        ];

        var matrixState = ParseViaMatrix(sharedArgs);
        var controlledState = ParseViaBinderLoop(sharedArgs);

        Assert.Equal(matrixState.OutputDirectory, controlledState.OutputDirectory);
        Assert.Equal(matrixState.ModelDirectory, controlledState.ModelDirectory);
        Assert.Equal(matrixState.Provider, controlledState.Provider);
        Assert.Equal(matrixState.FfmpegPath, controlledState.FfmpegPath);
        Assert.Equal(matrixState.FfprobePath, controlledState.FfprobePath);
        Assert.Equal(matrixState.ExpectedFixtureSha256, controlledState.ExpectedFixtureSha256);
        Assert.Equal(matrixState.TargetLanguage, controlledState.TargetLanguage);
        Assert.Equal(matrixState.SourceLanguage, controlledState.SourceLanguage);
        Assert.Equal(matrixState.Mode, controlledState.Mode);
        Assert.Equal(matrixState.RunCount, controlledState.RunCount);
        Assert.Equal(matrixState.ResourceTelemetryBounds, controlledState.ResourceTelemetryBounds);
        Assert.Equal("out", controlledState.OutputDirectory);
        Assert.Equal("fr", controlledState.TargetLanguage);
        Assert.Equal(3, controlledState.RunCount);
        Assert.Equal(37.25, controlledState.ResourceTelemetryBounds.MaxCpuPercent);
    }

    [Theory]
    [InlineData("--stages")]
    [InlineData("--stage")]
    [InlineData("--model")]
    [InlineData("--report-dir")]
    [InlineData("--bogus")]
    public void Command_specific_and_unknown_options_are_not_handled(string option)
    {
        var state = new ControlledBenchmarkCliOptions();
        using var error = new StringWriter();

        var result = ControlledBenchmarkCliBinder.TryApplyOption(option, "value", state, error);

        Assert.Equal(ControlledCliOptionResult.NotHandled, result);
        Assert.Null(state.OutputDirectory);
        Assert.Equal("es", state.TargetLanguage);
        Assert.Equal(1, state.RunCount);
        Assert.Equal(new Trackdub.Domain.Benchmarking.ResourceTelemetryBounds(), state.ResourceTelemetryBounds);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void Invalid_run_count_fails_with_the_historical_message(string value)
    {
        var state = new ControlledBenchmarkCliOptions();
        using var error = new StringWriter();

        var result = ControlledBenchmarkCliBinder.TryApplyOption("--runs", value, state, error);

        Assert.Equal(ControlledCliOptionResult.Failed, result);
        Assert.Equal(1, state.RunCount);
        Assert.Contains($"Invalid run count '{value}'. Expected a positive integer.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_resource_limit_fails_without_touching_prior_state()
    {
        var state = new ControlledBenchmarkCliOptions();
        using var error = new StringWriter();
        Assert.Equal(
            ControlledCliOptionResult.Applied,
            ControlledBenchmarkCliBinder.TryApplyOption("--max-cpu-percent", "50", state, error));

        var result = ControlledBenchmarkCliBinder.TryApplyOption("--max-cpu-percent", "bogus", state, error);

        Assert.Equal(ControlledCliOptionResult.Failed, result);
        Assert.Equal(50, state.ResourceTelemetryBounds.MaxCpuPercent);
        Assert.Contains("Invalid value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_value_reports_the_option_name()
    {
        string[] args = ["fixture.wav", "--output"];
        using var error = new StringWriter();
        int index = 1;

        Assert.False(ControlledBenchmarkCliBinder.TryReadValue(args, ref index, error, out _));
        Assert.Contains("Missing value for --output", error.ToString(), StringComparison.Ordinal);
    }

    private static ControlledBenchmarkCliOptions ParseViaMatrix(string[] sharedArgs)
    {
        using var error = new StringWriter();
        string[] args = ["fixture.wav", .. sharedArgs];

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(args, error, out var options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        return new ControlledBenchmarkCliOptions
        {
            OutputDirectory = options.OutputDirectory,
            ModelDirectory = options.ModelDirectory,
            Provider = options.Provider,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = options.FfprobePath,
            ExpectedFixtureSha256 = options.ExpectedFixtureSha256,
            TargetLanguage = options.TargetLanguage,
            SourceLanguage = options.SourceLanguage,
            Mode = options.Mode,
            RunCount = options.RunCount,
            ResourceTelemetryBounds = options.ResourceTelemetryBounds,
        };
    }

    private static ControlledBenchmarkCliOptions ParseViaBinderLoop(string[] sharedArgs)
    {
        string[] args = ["fixture.wav", .. sharedArgs];
        var state = new ControlledBenchmarkCliOptions();
        using var error = new StringWriter();

        for (int index = 1; index < args.Length; index++)
        {
            if (ControlledBenchmarkCliBinder.TryApplyFlag(args[index], state))
            {
                continue;
            }

            string option = args[index];
            Assert.True(ControlledBenchmarkCliBinder.TryReadValue(args, ref index, error, out string value));
            var result = ControlledBenchmarkCliBinder.TryApplyOption(option, value, state, error);
            Assert.Equal(ControlledCliOptionResult.Applied, result);
        }

        return state;
    }
}
