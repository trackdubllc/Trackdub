using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// Product / engine contract tests for the commercial-safe Spleeter 2stems lane.
/// Asserts absolute sherpa-onnx literals and production path construction
/// (no reflection on private engine fields).
/// </summary>
public sealed class SpleeterSeparationContractTests
{
    [Fact]
    public void Engine_family_name_is_spleeter()
    {
        Assert.Equal("spleeter", SpleeterStemSeparationEngine.EngineFamilyName);
    }

    [Fact]
    public void Target_sample_rate_matches_sherpa_onnx_spleeter_export()
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
    public void Production_model_paths_use_shared_relative_file_names()
    {
        // Same helper SpleeterOnnxSeparator uses; rooted names are rejected.
        string root = Path.Combine("cache", "csukuangfj", "sherpa-onnx-spleeter-2stems");
        string vocals = SpleeterModelConstants.ResolveModelPath(
            root, SpleeterModelConstants.VocalsModelFileName);
        string acc = SpleeterModelConstants.ResolveModelPath(
            root, SpleeterModelConstants.AccompanimentModelFileName);

        Assert.Equal(SpleeterModelConstants.ResolveModelPath(root, "vocals.onnx"), vocals);
        Assert.Equal(SpleeterModelConstants.ResolveModelPath(root, "accompaniment.onnx"), acc);
    }

    [Fact]
    public void Stft_processor_pad_block_matches_onnx_export_time_dimension()
    {
        Assert.Equal(512, SpleeterModelConstants.TimePad);
        Assert.Equal(SpleeterModelConstants.TimePad, SpleeterStftProcessor.PadTo);
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
