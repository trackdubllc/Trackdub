using Trackdub.Benchmarks;

namespace Trackdub.Benchmarks.Tests;

public sealed class ControlledStageBenchmarkMatrixTests
{
    [Fact]
    public void Parser_defaults_to_all_extended_stages()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.mp4", "--output", "out"],
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.Empty(options.Stages);
        Assert.Equal("fresh-process", options.Mode);
    }

    [Fact]
    public void Parser_accepts_stage_list_and_stage_model_overrides()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            [
                "fixture.mp4",
                "--output", "out",
                "--stages", "tts,asr",
                "--model", "asr=whisper-small,tts=kokoro",
                "--provider", "cpu",
            ],
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.Equal(["tts", "asr"], options.Stages);
        Assert.Equal("whisper-small", options.ModelOverrides["asr"]);
        Assert.Equal("kokoro", options.ModelOverrides["tts"]);
        Assert.Equal("cpu", options.Provider);
    }

    [Fact]
    public void Parser_rejects_unknown_stage()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.mp4", "--output", "out", "--stages", "asr,not-a-stage"],
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("Unknown pipeline stage", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_rejects_model_without_stage_qualifier()
    {
        using var error = new StringWriter();

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            ["fixture.mp4", "--output", "out", "--model", "whisper-small"],
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("stage=alias", error.ToString(), StringComparison.Ordinal);
    }
}
