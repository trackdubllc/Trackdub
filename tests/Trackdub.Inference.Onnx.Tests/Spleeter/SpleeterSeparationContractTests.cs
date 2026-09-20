using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// Product / engine contract tests for the commercial-safe Spleeter 2stems lane.
/// Asserts against <see cref="SpleeterModelConstants"/> — the same source the
/// separator and engine use for model filenames, sample rate, and STFT pad.
/// </summary>
public sealed class SpleeterSeparationContractTests
{
    [Fact]
    public void Engine_family_name_is_spleeter()
    {
        Assert.Equal("spleeter", SpleeterStemSeparationEngine.EngineFamilyName);
    }

    [Fact]
    public void Target_sample_rate_matches_deezer_spleeter_2stems_44k1()
    {
        Assert.Equal(44100, SpleeterModelConstants.TargetSampleRate);
    }

    [Fact]
    public void Model_file_names_match_sherpa_onnx_2stems_bundle()
    {
        Assert.Equal("vocals.onnx", SpleeterModelConstants.VocalsModelFileName);
        Assert.Equal("accompaniment.onnx", SpleeterModelConstants.AccompanimentModelFileName);
    }

    [Fact]
    public void Engine_private_constants_alias_shared_model_constants()
    {
        // Engine must not keep independent private literals that can drift from the separator.
        var vocals = typeof(SpleeterStemSeparationEngine).GetField(
            "VocalsModelFileName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("VocalsModelFileName constant missing on engine.");
        var accomp = typeof(SpleeterStemSeparationEngine).GetField(
            "AccompanimentModelFileName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("AccompanimentModelFileName constant missing on engine.");
        var sampleRate = typeof(SpleeterStemSeparationEngine).GetField(
            "TargetSampleRate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("TargetSampleRate constant missing on engine.");

        Assert.Equal(SpleeterModelConstants.VocalsModelFileName, vocals.GetValue(null));
        Assert.Equal(SpleeterModelConstants.AccompanimentModelFileName, accomp.GetValue(null));
        Assert.Equal(SpleeterModelConstants.TargetSampleRate, sampleRate.GetValue(null));
    }

    [Fact]
    public void Stft_processor_pad_block_matches_onnx_export_time_dimension()
    {
        Assert.Equal(SpleeterModelConstants.TimePad, SpleeterStftProcessor.PadTo);
        Assert.Equal(512, SpleeterModelConstants.TimePad);
    }

    [Fact]
    public void Stft_processor_forward_sizes_align_with_shared_stft_contract()
    {
        var processor = new SpleeterStftProcessor();
        var input = new float[SpleeterModelConstants.TargetSampleRate * 2];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Sin(2.0 * Math.PI * 440 * i / SpleeterModelConstants.TargetSampleRate) * 0.25f;
        }

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(input);
        Assert.Equal(0, targetFrames % SpleeterModelConstants.TimePad);
        Assert.Equal(
            SpleeterModelConstants.PadTimeFrames(
                1 + ((input.Length - SpleeterModelConstants.Nfft) / SpleeterModelConstants.Hop)),
            targetFrames);
        Assert.Equal(targetFrames * SpleeterModelConstants.MaxFreqBins, mag.Length);
        Assert.Equal(mag.Length, phase.Length);
    }
}
