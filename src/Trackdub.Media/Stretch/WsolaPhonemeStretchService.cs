using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Stretch;

public sealed class WsolaPhonemeStretchService : IPhonemeStretchService
{
    // Analysis window length in samples.
    private const int WindowSize = 1024;

    // Step size used for the OUTPUT buffer per synthesis frame (= window / 2 → 50 % overlap).
    // Named "analysisHop" in the brief; represents how far the output advances per frame.
    private const int OutputHop = 512;

    // Maximum source-position perturbation searched for the best-matching frame (±samples).
    private const int SearchDelta = 64;

    // Correlation window: half of OutputHop — enough to anchor the overlap without excess cost.
    private const int CorrelationLength = OutputHop / 2;

    public async Task<TimeSpan?> StretchAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<PhonemeStretchPlan> plan,
        CancellationToken cancellationToken)
    {
        if (plan.Count == 0)
            return null;

        if (plan.All(p => !p.WithinBounds))
            return null;

        WavePcm16Samples input = await WavePcm16
            .ReadAllSamplesAsync(inputPath, cancellationToken)
            .ConfigureAwait(false);

        int sampleRate = input.SampleRate;
        int channelCount = input.ChannelCount;
        int totalFrames = input.FrameCount;

        float[][] channels = Deinterleave(input.Samples, channelCount, totalFrames);

        var outputChannels = new List<float>[channelCount];
        for (int c = 0; c < channelCount; c++)
            outputChannels[c] = new List<float>(totalFrames);

        float[] hann = BuildHannWindow(WindowSize);

        IEnumerable<PhonemeStretchPlan> sortedPlan = plan
            .OrderBy(static p => p.OriginalStart);

        int currentFrame = 0;

        foreach (PhonemeStretchPlan entry in sortedPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int regionStart = Math.Clamp(
                (int)Math.Floor(entry.OriginalStart.TotalSeconds * sampleRate),
                0, totalFrames);
            int regionEnd = Math.Clamp(
                (int)Math.Ceiling(entry.OriginalEnd.TotalSeconds * sampleRate),
                regionStart, totalFrames);

            // Copy any gap that precedes this plan entry.
            if (regionStart > currentFrame)
            {
                AppendRegion(channels, outputChannels, currentFrame,
                    regionStart - currentFrame, channelCount);
                currentFrame = regionStart;
            }

            int regionLength = regionEnd - currentFrame;
            if (regionLength <= 0)
                continue;

            bool shouldCopyDirect =
                !entry.WithinBounds
                || Math.Abs(entry.StretchRatio - 1.0) < 1e-9
                || regionLength < WindowSize;

            if (shouldCopyDirect)
            {
                AppendRegion(channels, outputChannels, currentFrame,
                    regionLength, channelCount);
            }
            else
            {
                // sourceHop = round(OutputHop / ratio):
                //   source advances by sourceHop per frame;
                //   output advances by OutputHop per frame.
                //   → output length ≈ sourceLength * OutputHop / sourceHop
                //                   ≈ sourceLength * ratio.
                int sourceHop = Math.Max(1,
                    (int)Math.Round(OutputHop / entry.StretchRatio));

                for (int c = 0; c < channelCount; c++)
                {
                    float[] stretched = WsolaStretch(
                        channels[c], currentFrame, regionLength, sourceHop, hann);
                    outputChannels[c].AddRange(stretched);
                }
            }

            currentFrame = regionEnd;
        }

        // Copy any audio that follows the last plan entry.
        if (currentFrame < totalFrames)
        {
            AppendRegion(channels, outputChannels, currentFrame,
                totalFrames - currentFrame, channelCount);
        }

        int outputFrameCount = outputChannels[0].Count;
        float[] interleaved = Interleave(outputChannels, channelCount, outputFrameCount);

        await WavePcm16
            .WriteSamplesAsync(outputPath, interleaved, sampleRate, channelCount, cancellationToken)
            .ConfigureAwait(false);

        return TimeSpan.FromSeconds((double)outputFrameCount / sampleRate);
    }

    // ---------------------------------------------------------------------------
    // WSOLA core
    // ---------------------------------------------------------------------------

    private static float[] WsolaStretch(
        float[] source,
        int sourceOffset,
        int sourceLength,
        int sourceHop,
        float[] hann)
    {
        if (sourceLength <= 0)
            return [];

        // Total synthesis frames needed to consume the full source region.
        int numFrames = (int)Math.Ceiling((double)sourceLength / sourceHop);

        // Expected output sample count.
        int expectedLength = (int)Math.Round((double)sourceLength * OutputHop / sourceHop);

        int bufferSize = numFrames * OutputHop + WindowSize;
        var outputBuffer = new float[bufferSize];
        var normBuffer = new float[bufferSize];

        for (int k = 0; k < numFrames; k++)
        {
            int nomSourcePos = k * sourceHop;   // nominal source position
            int outputPos = k * OutputHop;       // write position in output

            // Search window for best source position (skip correlation on first frame).
            int searchMin = Math.Max(0, nomSourcePos - SearchDelta);
            int searchMax = Math.Min(sourceLength - WindowSize, nomSourcePos + SearchDelta);
            if (searchMax < 0)
                searchMax = 0;
            if (searchMax < searchMin)
                searchMax = searchMin;

            int bestPos = k == 0
                ? nomSourcePos
                : FindBestSourcePosition(
                    source, sourceOffset, outputBuffer, outputPos,
                    searchMin, searchMax);

            // Overlap-add: window the source grain and accumulate into the output.
            int grainSamples = Math.Min(WindowSize, sourceLength - bestPos);
            for (int n = 0; n < grainSamples; n++)
            {
                int outIdx = outputPos + n;
                if (outIdx >= bufferSize)
                    break;

                float w = hann[n];
                outputBuffer[outIdx] += w * source[sourceOffset + bestPos + n];
                normBuffer[outIdx] += w;
            }
        }

        // Trim and normalise.
        int trimLength = Math.Min(expectedLength, bufferSize);
        var result = new float[trimLength];
        for (int i = 0; i < trimLength; i++)
        {
            float norm = normBuffer[i];
            result[i] = norm > 1e-9f ? outputBuffer[i] / norm : 0f;
        }

        return result;
    }

    // Cross-correlate the last CorrelationLength output samples against each source
    // candidate; return the candidate offset that maximises the dot product.
    private static int FindBestSourcePosition(
        float[] source,
        int sourceOffset,
        float[] outputSoFar,
        int outputPos,
        int searchMin,
        int searchMax)
    {
        int outStart = outputPos - CorrelationLength;
        int bestPos = searchMin;
        double bestCorr = double.MinValue;

        for (int candidate = searchMin; candidate <= searchMax; candidate++)
        {
            double corr = 0.0;
            for (int n = 0; n < CorrelationLength; n++)
            {
                int outIdx = outStart + n;
                if (outIdx < 0 || outIdx >= outputSoFar.Length)
                    continue;

                int srcIdx = sourceOffset + candidate + n;
                if (srcIdx >= source.Length)
                    break;

                corr += (double)outputSoFar[outIdx] * source[srcIdx];
            }

            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestPos = candidate;
            }
        }

        return bestPos;
    }

    // ---------------------------------------------------------------------------
    // Window / channel helpers
    // ---------------------------------------------------------------------------

    private static float[] BuildHannWindow(int size)
    {
        var window = new float[size];
        double factor = 2.0 * Math.PI / (size - 1);
        for (int n = 0; n < size; n++)
            window[n] = 0.5f - 0.5f * (float)Math.Cos(factor * n);
        return window;
    }

    private static void AppendRegion(
        float[][] channels,
        List<float>[] outputChannels,
        int offset,
        int length,
        int channelCount)
    {
        for (int c = 0; c < channelCount; c++)
        {
            int available = channels[c].Length - offset;
            int safeLength = Math.Min(length, Math.Max(0, available));
            if (safeLength > 0)
                outputChannels[c].AddRange(channels[c].AsSpan(offset, safeLength));
        }
    }

    private static float[][] Deinterleave(
        float[] interleaved,
        int channelCount,
        int frameCount)
    {
        var channels = new float[channelCount][];
        for (int c = 0; c < channelCount; c++)
            channels[c] = new float[frameCount];

        for (int f = 0; f < frameCount; f++)
        {
            for (int c = 0; c < channelCount; c++)
                channels[c][f] = interleaved[f * channelCount + c];
        }

        return channels;
    }

    private static float[] Interleave(
        List<float>[] outputChannels,
        int channelCount,
        int frameCount)
    {
        var result = new float[frameCount * channelCount];
        for (int f = 0; f < frameCount; f++)
        {
            for (int c = 0; c < channelCount; c++)
                result[f * channelCount + c] = outputChannels[c][f];
        }

        return result;
    }
}
