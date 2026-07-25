using Trackdub.Media.Mixing;

namespace Trackdub.Media.Tests;

public sealed class RoomToneConvolverTests
{
    [Fact]
    public void TryApply_returns_non_null_for_sufficient_non_silent_preroll()
    {
        float[] input = CreateSineWave(length: 1000);
        float[] preRoll = CreateSineWave(length: 512);

        float[]? result = RoomToneConvolver.TryApply(input, preRoll);

        Assert.NotNull(result);
    }

    [Fact]
    public void TryApply_output_length_equals_input_length()
    {
        float[] input = CreateSineWave(length: 2000);
        float[] preRoll = CreateSineWave(length: 512);

        float[]? result = RoomToneConvolver.TryApply(input, preRoll);

        Assert.NotNull(result);
        Assert.Equal(input.Length, result!.Length);
    }

    [Fact]
    public void TryApply_output_differs_from_dry_input()
    {
        float[] input = CreateSineWave(length: 1000, frequencyMultiplier: 1f);
        float[] preRoll = CreateSineWave(length: 512, frequencyMultiplier: 7f);

        float[]? result = RoomToneConvolver.TryApply(input, preRoll);

        Assert.NotNull(result);
        bool anyDifference = result!.Zip(input).Any(pair => Math.Abs(pair.First - pair.Second) > 1e-6f);
        Assert.True(anyDifference, "Expected wet convolution to produce output different from dry input.");
    }

    [Fact]
    public void TryApply_returns_null_when_preroll_below_minimum_length()
    {
        float[] input = CreateSineWave(length: 1000);
        float[] shortPreRoll = CreateSineWave(length: 64); // below MinIrSamples (128)

        float[]? result = RoomToneConvolver.TryApply(input, shortPreRoll);

        Assert.Null(result);
    }

    [Fact]
    public void TryApply_returns_null_for_empty_preroll()
    {
        float[] input = CreateSineWave(length: 1000);

        float[]? result = RoomToneConvolver.TryApply(input, ReadOnlySpan<float>.Empty);

        Assert.Null(result);
    }

    [Fact]
    public void TryApply_returns_null_when_preroll_is_silence()
    {
        float[] input = CreateSineWave(length: 1000);
        float[] silentPreRoll = new float[512]; // all zeros

        float[]? result = RoomToneConvolver.TryApply(input, silentPreRoll);

        Assert.Null(result);
    }

    [Fact]
    public void TryApply_returns_null_for_empty_input()
    {
        float[] preRoll = CreateSineWave(length: 512);

        float[]? result = RoomToneConvolver.TryApply(ReadOnlySpan<float>.Empty, preRoll);

        Assert.Null(result);
    }

    [Fact]
    public void TryApply_amplitude_stays_within_safe_range_for_normalized_inputs()
    {
        float[] input = CreateSineWave(length: 2000, amplitude: 0.9f);
        float[] preRoll = CreateSineWave(length: 512, amplitude: 0.9f, frequencyMultiplier: 3f);

        float[]? result = RoomToneConvolver.TryApply(input, preRoll);

        Assert.NotNull(result);
        Assert.All(result!, sample => Assert.InRange(sample, -2f, 2f));
    }

    [Fact]
    public void TryApply_longer_preroll_than_max_ir_samples_still_applies_reverb()
    {
        float[] input = CreateSineWave(length: 2000);
        // 2048 > MaxIrSamples (1024) — should truncate to tail and still apply
        float[] longPreRoll = CreateSineWave(length: 2048);

        float[]? result = RoomToneConvolver.TryApply(input, longPreRoll);

        Assert.NotNull(result);
        Assert.Equal(input.Length, result!.Length);
    }

    private static float[] CreateSineWave(int length, float amplitude = 0.5f, float frequencyMultiplier = 1f)
    {
        var samples = new float[length];
        for (int i = 0; i < length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(2d * Math.PI * frequencyMultiplier * i / length);
        }

        return samples;
    }
}
