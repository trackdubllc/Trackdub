namespace Trackdub.Media.Waveforms;

/// <summary>
/// Estimates male/female from short PCM using median F0. Returns null when the
/// clip does not have enough voiced frames for an honest guess.
/// </summary>
internal static class SpeechPitchGenderEstimator
{
    internal const double FemaleThresholdHz = 165d;
    private const double MinHz = 70d;
    private const double MaxHz = 300d;
    private const double VoicedAmplitudeThreshold = 0.015d;
    private const double MinNormalizedCorrelation = 0.25d;
    private const int MinVoicedWindows = 4;
    private const int WindowMilliseconds = 40;

    public static string? EstimateGender(ReadOnlySpan<float> samples, int sampleRate)
    {
        if (sampleRate < 8000 || samples.Length < sampleRate / 5)
        {
            return null;
        }

        int minLag = Math.Max(1, (int)Math.Round(sampleRate / MaxHz));
        int maxLag = Math.Max(minLag + 1, (int)Math.Round(sampleRate / MinHz));
        int windowFrames = Math.Max(maxLag + 8, sampleRate * WindowMilliseconds / 1000);
        int hopFrames = Math.Max(1, windowFrames / 2);
        if (samples.Length < windowFrames)
        {
            return null;
        }

        List<double> pitches = [];
        for (int start = 0; start + windowFrames <= samples.Length; start += hopFrames)
        {
            ReadOnlySpan<float> window = samples.Slice(start, windowFrames);
            if (!IsVoiced(window))
            {
                continue;
            }

            if (TryEstimateF0(window, sampleRate, minLag, maxLag, out double f0))
            {
                pitches.Add(f0);
            }
        }

        if (pitches.Count < MinVoicedWindows)
        {
            return null;
        }

        pitches.Sort();
        double median = pitches[pitches.Count / 2];
        return median >= FemaleThresholdHz ? "female" : "male";
    }

    private static bool IsVoiced(ReadOnlySpan<float> window)
    {
        double sum = 0d;
        for (int i = 0; i < window.Length; i++)
        {
            sum += Math.Abs(window[i]);
        }

        return window.Length > 0 && (sum / window.Length) >= VoicedAmplitudeThreshold;
    }

    private static bool TryEstimateF0(
        ReadOnlySpan<float> window,
        int sampleRate,
        int minLag,
        int maxLag,
        out double f0)
    {
        f0 = 0d;
        int usableMaxLag = Math.Min(maxLag, window.Length - 1);
        if (usableMaxLag <= minLag)
        {
            return false;
        }

        double energy = 0d;
        for (int i = 0; i < window.Length; i++)
        {
            energy += window[i] * window[i];
        }

        if (energy < 1e-8d)
        {
            return false;
        }

        double meanEnergy = energy / window.Length;
        double bestCorr = double.MinValue;
        double[] correlations = new double[usableMaxLag - minLag + 1];
        for (int lag = minLag; lag <= usableMaxLag; lag++)
        {
            double corr = 0d;
            int count = window.Length - lag;
            for (int i = 0; i < count; i++)
            {
                corr += window[i] * window[i + lag];
            }

            corr /= count;
            correlations[lag - minLag] = corr;
            if (corr > bestCorr)
            {
                bestCorr = corr;
            }
        }

        if (bestCorr / meanEnergy < MinNormalizedCorrelation)
        {
            return false;
        }

        double peakFloor = bestCorr * 0.9d;
        int bestLag = minLag;
        for (int lag = minLag; lag <= usableMaxLag; lag++)
        {
            if (correlations[lag - minLag] >= peakFloor)
            {
                bestLag = lag;
                break;
            }
        }

        f0 = sampleRate / (double)bestLag;
        return f0 is >= MinHz and <= MaxHz;
    }
}
