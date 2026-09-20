using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// STFT parity locks for the sherpa-onnx Spleeter 2stems export path
/// (scripts/spleeter/separate_onnx.py): n_fft=4096, hop=1024, keep 1024 bins,
/// pad time to a multiple of 512, periodic Hann, center=false.
/// </summary>
public sealed class SpleeterStftParityTests
{
    private const int Nfft = 4096;
    private const int Hop = 1024;
    private const int MaxFreqs = 1024;
    private const int PadTo = 512;
    private const int SampleRate = 44100;

    private static int ExpectedBaseFrames(int sampleCount) =>
        sampleCount >= Nfft ? 1 + ((sampleCount - Nfft) / Hop) : 1;

    private static int ExpectedTargetFrames(int sampleCount)
    {
        int baseFrames = ExpectedBaseFrames(sampleCount);
        int remainder = baseFrames % PadTo;
return baseFrames + (PadTo - remainder);
    }

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
    public void Forward_pads_time_frames_to_multiple_of_512()
    {
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate * 2, 440);

        (_, _, int targetFrames) = processor.Forward(sine);

        Assert.Equal(ExpectedTargetFrames(sine.Length), targetFrames);
        Assert.Equal(0, targetFrames % PadTo);
        Assert.True(targetFrames >= ExpectedBaseFrames(sine.Length));
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

    [Fact]
    public void Window_is_periodic_hann_matching_0_5_times_1_minus_cos_2pi_i_over_nfft()
    {
        // Spleeter / sherpa-onnx use periodic Hann (period N, not N-1).
        var samples = MakeSine(Nfft, 440, amplitude: 1.0);
        var processor = new SpleeterStftProcessor();

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(samples);
        Assert.Equal(PadTo, targetFrames);
        Assert.Equal(targetFrames * MaxFreqs, mag.Length);
        Assert.Equal(targetFrames * MaxFreqs, phase.Length);
        _ = phase;

        // Reconstruct first-frame windowed input energy via known periodic Hann peak at i=N/2.
        double expectedPeakWindow = 1.0;
        double actualPeakWindow = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * (Nfft / 2) / Nfft));
    [Fact]
    public void Window_is_periodic_hann_matching_0_5_times_1_minus_cos_2pi_i_over_nfft()
    {
        // A unit impulse at sample i is windowed to w[i]; the NoScaling FFT of an
        // impulse has unit magnitude across all bins, so Forward's kept-bin
        // magnitudes must equal the analytic periodic-Hann value at that tap.
        var processor = new SpleeterStftProcessor();

        foreach (int sample in new[] { 1, Nfft / 4, Nfft / 2, Nfft - 1 })
        {
            var impulse = new float[Nfft];
            impulse[sample] = 1f;

            (float[] mag, _, _) = processor.Forward(impulse);

            double expected = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * sample / Nfft));
            for (int k = 0; k < MaxFreqs; k++)
            {
                Assert.InRange(mag[k], (float)expected - 1e-3f, (float)expected + 1e-3f);
            }
        }
    }
        Assert.InRange(expectedBin, 0, MaxFreqs - 1);

        // Inspect an early frame that is fully inside the signal (not tail zero-pad).
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
        // Parity intent: iSTFT(Forward) with full kept-bin magnitude/phase ≈ original
        // for band-limited content inside the first 1024 bins (Nyquist half of 4096).
        // 440 Hz is well inside that band; HF bins are zeroed by design (sherpa mask pad).
        var processor = new SpleeterStftProcessor();
        float[] sine = MakeSine(SampleRate, 440, amplitude: 0.5);

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(sine);
        float[] reconstructed = processor.Inverse(mag, phase, targetFrames, sine.Length);

        Assert.Equal(sine.Length, reconstructed.Length);

        // Skip edges where WOLA window-sum / STFT framing are least accurate.
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
        Assert.True(rmsError < 0.25 * rmsSignal + 1e-4,
            $"RMS error {rmsError} too large vs signal RMS {rmsSignal} (scale={scale}).");
    }

    [Fact]
    public void Forward_out_of_range_samples_are_zero_padded_not_wrapped()
    {
        var processor = new SpleeterStftProcessor();
        var impulse = new float[Nfft];
        impulse[0] = 1f;

        (float[] mag, float[] phase, int targetFrames) = processor.Forward(impulse);

        Assert.Equal(PadTo, targetFrames);
        // Later frames start past the impulse; magnitude should be ~0 there.
        int lateFrame = PadTo - 1;
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
