using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests;

public sealed class DubbingBenchmarkOptionsTests : IDisposable
{
    private readonly string _inputPath = Path.Join(Path.GetTempPath(), $"dubopt-{Guid.NewGuid():N}.mp4");

    public DubbingBenchmarkOptionsTests()
    {
        File.WriteAllBytes(_inputPath, [0x00]);
    }

    public void Dispose() => File.Delete(_inputPath);

    [Fact]
    public void TryParse_accepts_repeated_model_and_provider_pins_with_canonical_keys()
    {
        using var error = new StringWriter();
        string[] args =
        [
            _inputPath,
            "--model", "ASR=whisper-small",
            "--model", "tts=kokoro",
            "--provider", "Asr=cpu",
            "--provider", "TTS=directml",
        ];

        bool parsed = DubbingBenchmarkOptions.TryParse(args, error, out DubbingBenchmarkOptions? options);

        Assert.True(parsed, error.ToString());
        Assert.NotNull(options);
        Assert.Equal("whisper-small", options!.ModelPins![StageNames.Asr]);
        Assert.Equal("kokoro", options.ModelPins[StageNames.Tts]);
        Assert.Equal("cpu", options.ProviderPins![StageNames.Asr]);
        Assert.Equal("directml", options.ProviderPins[StageNames.Tts]);
        Assert.Equal(2, options.ModelPins.Count);
        Assert.Equal(2, options.ProviderPins.Count);
    }

    [Fact]
    public void TryParse_without_pins_leaves_dictionaries_null()
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse([_inputPath], error, out DubbingBenchmarkOptions? options);

        Assert.True(parsed, error.ToString());
        Assert.Null(options!.ModelPins);
        Assert.Null(options.ProviderPins);
    }

    [Theory]
    [InlineData("--model", "asr")]            // missing '='
    [InlineData("--model", "=whisper-small")] // blank stage
    [InlineData("--model", "asr=")]           // blank alias
    [InlineData("--provider", "tts")]         // missing '='
    [InlineData("--provider", "=cpu")]
    [InlineData("--provider", "tts=")]
    public void TryParse_rejects_malformed_pin(string flag, string pin)
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse([_inputPath, flag, pin], error, out _);

        Assert.False(parsed);
        Assert.Contains(flag, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_rejects_duplicate_model_stage_case_insensitively()
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--model", "asr=whisper-small", "--model", "ASR=whisper-large"],
            error,
            out _);

        Assert.False(parsed);
        Assert.Contains("duplicate", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--model", "export=kokoro")]
    [InlineData("--provider", "export=cpu")]
    [InlineData("--provider", "not-a-stage=cpu")]
    public void TryParse_rejects_non_runtime_stage_pin(string flag, string pin)
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse([_inputPath, flag, pin], error, out _);

        Assert.False(parsed);
        Assert.Contains("does not accept a pin", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_rejects_duplicate_provider_stage_case_insensitively()
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--provider", "asr=cpu", "--provider", "ASR=directml"],
            error,
            out _);

        Assert.False(parsed);
        Assert.Contains("duplicate", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_rejects_model_and_provider_pins_for_unmapped_speech_enhancement()
    {
        // speech-enhancement has no model-alias mapping in BuildModelPreferences — a
        // --model pin for it would be silently ignored downstream, so it is rejected.
        using var modelError = new StringWriter();
        Assert.False(DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--model", "speech-enhancement=some-model"],
            modelError,
            out _));
        Assert.Contains("does not accept a pin", modelError.ToString(), StringComparison.Ordinal);

        using var providerError = new StringWriter();
        Assert.False(DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--provider", "speech-enhancement=cpu"],
            providerError,
            out _));
        Assert.Contains("does not accept a pin", providerError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_rejects_invalid_provider_label()
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--provider", "asr=quantum"],
            error,
            out _);

        Assert.False(parsed);
        Assert.Contains("not a valid execution provider", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_allows_same_stage_in_model_and_provider_dictionaries()
    {
        using var error = new StringWriter();

        bool parsed = DubbingBenchmarkOptions.TryParse(
            [_inputPath, "--model", "asr=whisper-small", "--provider", "asr=cpu"],
            error,
            out DubbingBenchmarkOptions? options);

        Assert.True(parsed, error.ToString());
        Assert.Equal("whisper-small", options!.ModelPins![StageNames.Asr]);
        Assert.Equal("cpu", options.ProviderPins![StageNames.Asr]);
    }
}
