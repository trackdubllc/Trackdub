using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// STFT parity locks for the sherpa-onnx Spleeter 2stems export path
/// (scripts/spleeter/separate_onnx.py). Absolute contract values are asserted
/// on <see cref="SpleeterModelConstants"/> so a constants regression cannot
/// silently pass by changing both implementation and test oracle together.
/// </summary>
public sealed class SpleeterStftParityTests
{
    // Literals lock the sherpa contract independently of SpleeterModelConstants.
    private const int Nfft = 4096;
    private const int Hop = 1024;
    private const int MaxFreqs = 1024;
    private const int PadTo = 512;
    private const int SampleRate = 44100;

    private static int ExpectedBaseFrames(int sampleCount) =>
        sampleCount >= Nfft ? 1 + ((sampleCount - Nfft) / Hop) : 1;

    private static int ExpectedTargetFrames(int sampleCount) =>
        SpleeterModelConstants.PadTimeFrames(ExpectedBaseFrames(sampleCount));

    private static float[] MakeSine(int sampleCount, double frequencyHz, double amplitude = 0.5)
    {
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / SampleRate));
        }

        return samples;
    }

    [Fact]
    public void Constants_match_sherpa_onnx_stft_literals()
    {
        Assert.Equal(Nfft, SpleeterModelConstants.Nfft);
        Assert.Equal(Hop, SpleeterModelConstants.Hop);
        Assert.Equal(MaxFreqs, SpleeterModelConstants.MaxFreqBins);
        Assert.Equal(PadTo, SpleeterModelConstants.TimePad);
        Assert.Equal(SampleRate, SpleeterModelConstants.TargetSampleRate);
        Assert.Equal(PadTo, SpleeterStftProcessor.PadTo);
    }

    [Fact]
    public void PadTimeFrames_matches_sherpa_rule_including_exact_multiples()
    {
        // separate_onnx.py: padding = 512 - (n % 512); always > 0 for n > 0.
        Assert.Equal(2 * PadTo, SpleeterModelConstants.PadTimeFrames(PadTo));
        Assert.Equal(3 * PadTo, SpleeterModelConstants.PadTimeFrames(2 * PadTo));
        Assert.Equal(PadTo, SpleeterModelConstants.PadTimeFrames(1));
        Assert.Equal(2 * PadTo, SpleeterModelConstants.PadTimeFrames(PadTo + 1));
        Assert.Equal(PadTo, SpleeterModelConstants.PadTimeFrames(0));
    }

    [Fact]
    public void Forward_pads_time_frames_to_multiple_of_512()
    {
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate * 2, 440);

        (_, _, int targetFrames) = processor.Forward(sine);

        Assert.Equal(ExpectedTargetFrames(sine.Length), targetFrames);
        Assert.Equal(0, targetFrames % PadTo);
        Assert.True(targetFrames > ExpectedBaseFrames(sine.Length));
    }

    [Fact]
    public void Forward_exact_frame_multiple_gets_additional_sherpa_pad_block()
    {
        var processor = new SpleeterStftProcessor();
        int sampleCount = Nfft + ((PadTo - 1) * Hop);
        Assert.Equal(PadTo, ExpectedBaseFrames(sampleCount));

        float[] samples = MakeSine(sampleCount, 440, amplitude: 0.25);
        (_, _, int targetFrames) = processor.Forward(samples);

        Assert.Equal(2 * PadTo, targetFrames);
    }

    [Fact]
    public void Forward_short_input_yields_at_least_one_padded_frame_block()
    {
        var processor = new SpleeterStftProcessor();
        float[] shortInput = MakeSine(Nfft - 1, 440);

        (_, _, int targetFrames) = processor.Forward(shortInput);

        Assert.Equal(1, ExpectedBaseFrames(shortInput.Length));
        Assert.Equal(PadTo, targetFrames);
    }

    [Fact]
    public void Forward_magnitude_and_phase_lengths_are_targetFrames_times_1024()
    {
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate * 2, 440);

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(sine);

        Assert.Equal(targetFrames * MaxFreqs, mag.Length);
        Assert.Equal(targetFrames * MaxFreqs, phase.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(3072)]
    public void Forward_unit_impulse_mag_at_bin_zero_matches_periodic_hann_tap(int sampleIndex)
    {
        // Locks the production window via STFT output, not a standalone formula.
        // FFT of windowed impulse e[sampleIndex] → X[k] = w[sampleIndex] for all k;
        // kept-bin magnitude at k=0 is |w[sampleIndex]|.
        var processor = new SpleeterStftProcessor();
        var impulse = new float[Nfft];
        impulse[sampleIndex] = 1f;

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(impulse);
        Assert.Equal(SpleeterModelConstants.PadTimeFrames(1), targetFrames);

        double expectedWindow =
            0.5 * (1.0 - Math.Cos(2.0 * Math.PI * sampleIndex / Nfft));
        float actual = mag[0];
        Assert.InRange(actual, (float)expectedWindow - 1e-3f, (float)expectedWindow + 1e-3f);
        Assert.Equal(0f, phase[0], 3);
    }

    [Theory]
    [InlineData(440)]
    [InlineData(1000)]
    public void Forward_sine_peaks_near_expected_frequency_bin(int frequencyHz)
    {
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate * 2, frequencyHz, amplitude: 0.5);

        (float[] mag, _, int targetFrames) = processor.Forward(sine);
        int expectedBin = (int)Math.Round((double)frequencyHz * Nfft / SampleRate);
        Assert.InRange(expectedBin, 0, MaxFreqs - 1);

        int frame = Math.Min(4, Math.Max(0, ExpectedBaseFrames(sine.Length) - 2));
        int offset = frame * MaxFreqs;

        int peakBin = 0;
        float peak = mag[offset];
        for (int k = 1; k < MaxFreqs; k++)
        {
            if (mag[offset + k] > peak)
            {
                peak = mag[offset + k];
                peakBin = k;
            }
        }

        Assert.InRange(peakBin, expectedBin - 2, expectedBin + 2);
        Assert.True(peak > 0f);
        Assert.True(targetFrames >= 2);
    }

    [Fact]
    public void Inverse_reconstructs_signal_with_mask_identity_on_kept_bins()
    {
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate, 440, amplitude: 0.5);

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(sine);
        float[] reconstructed = processor.Inverse(mag, phase, targetFrames, sine.Length);

        Assert.Equal(sine.Length, reconstructed.Length);

        int start = Hop * 2;
        int end = Math.Min(sine.Length - Hop, ExpectedBaseFrames(sine.Length) * Hop);
        Assert.True(end > start);

        double num = 0, den = 0;
        for (int i = start; i < end; i++)
        {
            num += (double)sine[i] * reconstructed[i];
            den += (double)sine[i] * sine[i];
        }

        Assert.True(den > 0);
        double scale = num / den;
        Assert.InRange(scale, 0.7, 1.3);

        double rmsError = 0;
        for (int i = start; i < end; i++)
        {
            double err = sine[i] - (scale * reconstructed[i]);
            rmsError += err * err;
        }

        rmsError = Math.Sqrt(rmsError / (end - start));
        double rmsSignal = Math.Sqrt(den / (end - start));
        Assert.True(rmsError < (0.25 * rmsSignal) + 1e-4,
            $"RMS error {rmsError} too large vs signal RMS {rmsSignal} (scale={scale}).");
    }

    [Fact]
    public void Forward_out_of_range_samples_are_zero_padded_not_wrapped()
    {
        var processor = new SpleeterStftProcessor();
        var impulse = new float[Nfft];
        impulse[0] = 1f;

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(impulse);

        Assert.Equal(SpleeterModelConstants.PadTimeFrames(1), targetFrames);
        int lateFrame = targetFrames - 1;
        int offset = lateFrame * MaxFreqs;
        float lateEnergy = 0f;
        for (int k = 0; k < MaxFreqs; k++)
        {
            lateEnergy += mag[offset + k];
        }

        Assert.True(lateEnergy < 1e-3f, $"Late-frame energy {lateEnergy} suggests wrap-around instead of zero pad.");
        Assert.True(phase.Length == mag.Length);
    }
}
