using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// Product / engine contract tests for the commercial-safe Spleeter 2stems lane.
/// These do not run ONNX models; they lock manifest wiring and documented behavior.
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
        // Deep in engine: private const int TargetSampleRate = 44100.
        // Reflect to keep the constant from silently drifting.
        var field = typeof(SpleeterStemSeparationEngine)
            .GetField("TargetSampleRate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(44100, field!.GetValue(null));
    }

    [Fact]
    public void Model_file_names_match_sherpa_onnx_2stems_bundle()
    {
        var vocals = typeof(SpleeterStemSeparationEngine)
            .GetField("VocalsModelFileName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var accomp = typeof(SpleeterStemSeparationEngine)
            .GetField("AccompanimentModelFileName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(vocals);
        Assert.NotNull(accomp);
        Assert.Equal("vocals.onnx", vocals!.GetValue(null));
        Assert.Equal("accompaniment.onnx", accomp!.GetValue(null));
    }

    [Fact]
    public void Stft_processor_pad_block_matches_onnx_export_time_dimension()
    {
        // ONNX I/O: [2, num_splits, 512, 1024] — PadTo is the time chunk size.
        Assert.Equal(512, SpleeterStftProcessor.PadTo);
    }

    [Fact]
    public void Stft_processor_public_constants_align_with_sherpa_stft_config()
    {
        // n_fft / hop / freq bins are private; PadTo public. Cross-check via Forward sizing.
        var processor = new SpleeterStftProcessor();
        var input = new float[44100 * 2];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)Math.Sin(2.0 * Math.PI * 440 * i / 44100) * 0.25f;
        }

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(input);
        Assert.Equal(0, targetFrames % 512);
        Assert.Equal(targetFrames * 1024, mag.Length);
        Assert.Equal(mag.Length, phase.Length);
    }
}
