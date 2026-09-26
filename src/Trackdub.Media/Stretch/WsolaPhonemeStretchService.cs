using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Stretch;

public sealed class WsolaPhonemeStretchService : IPhonemeStretchService
{
    public async Task<PhonemeStretchResult?> StretchAsync(
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

        var outcomes = new List<PhonemeStretchRegionResult>(plan.Count);
        IEnumerable<(PhonemeStretchPlan Entry, int Index)> sortedPlan = plan
            .Select(static (entry, index) => (Entry: entry, Index: index))
            .OrderBy(static p => p.Entry.OriginalStart);

        int currentFrame = 0;

        foreach ((PhonemeStretchPlan entry, int index) in sortedPlan)
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
            {
                outcomes.Add(new(index, PhonemeStretchRegionStatus.TooShort));
                continue;
            }

            int windowSize = Math.Min(Math.Max(64, sampleRate / 50), regionLength / 2);
            int outputHop = windowSize / 2;
            int searchDelta = Math.Max(1, sampleRate / 250);

            bool shouldCopyDirect =
                !entry.WithinBounds
                || Math.Abs(entry.StretchRatio - 1.0) < 1e-9
                || windowSize < 64;

            if (shouldCopyDirect)
            {
                PhonemeStretchRegionStatus status = !entry.WithinBounds
                    ? entry.PlanningReason == "unmatched_viseme"
                        ? PhonemeStretchRegionStatus.Unmatched
                        : PhonemeStretchRegionStatus.UnsafeRatio
                    : windowSize < 64
                        ? PhonemeStretchRegionStatus.TooShort
                        : PhonemeStretchRegionStatus.Unchanged;
                outcomes.Add(new(index, status));
                AppendRegion(channels, outputChannels, currentFrame,
                    regionLength, channelCount);
            }
            else
            {
                // sourceHop = round(outputHop / ratio):
                //   source advances by sourceHop per frame;
                //   output advances by outputHop per frame.
                //   → output length ≈ sourceLength * outputHop / sourceHop
                //                   ≈ sourceLength * ratio.
                int sourceHop = Math.Max(1,
                    (int)Math.Round(outputHop / entry.StretchRatio));
                float[] hann = BuildHannWindow(windowSize);

                for (int c = 0; c < channelCount; c++)
                {
                    float[] stretched = WsolaStretch(
                        channels[c], currentFrame, regionLength, sourceHop,
                        outputHop, searchDelta, hann);
                    outputChannels[c].AddRange(stretched);
                }
                outcomes.Add(new(index, PhonemeStretchRegionStatus.Stretched));
            }

            currentFrame = regionEnd;
        }

        // Copy any audio that follows the last plan entry.
        if (currentFrame < totalFrames)
        {
            AppendRegion(channels, outputChannels, currentFrame,
                totalFrames - currentFrame, channelCount);
        }

        if (!outcomes.Any(static o => o.Status == PhonemeStretchRegionStatus.Stretched))
            return null;

        int outputFrameCount = outputChannels[0].Count;
        float[] interleaved = Interleave(outputChannels, channelCount, outputFrameCount);

        await WavePcm16
            .WriteSamplesAsync(outputPath, interleaved, sampleRate, channelCount, cancellationToken)
            .ConfigureAwait(false);

        return new PhonemeStretchResult(
            TimeSpan.FromSeconds((double)outputFrameCount / sampleRate), outcomes);
    }

    // ---------------------------------------------------------------------------
    // WSOLA core
    // ---------------------------------------------------------------------------

    private static float[] WsolaStretch(
        float[] source,
        int sourceOffset,
        int sourceLength,
        int sourceHop,
        int outputHop,
        int searchDelta,
        float[] hann)
    {
        if (sourceLength <= 0)
            return [];

        // Total synthesis frames needed to consume the full source region.
        int numFrames = (int)Math.Ceiling((double)sourceLength / sourceHop);

        // Expected output sample count.
        int windowSize = hann.Length;
        int correlationLength = outputHop / 2;
        int expectedLength = (int)Math.Round((double)sourceLength * outputHop / sourceHop);

        int bufferSize = (numFrames * outputHop) + windowSize;
        var outputBuffer = new float[bufferSize];
        var normBuffer = new float[bufferSize];

        for (int k = 0; k < numFrames; k++)
        {
            int nomSourcePos = k * sourceHop;   // nominal source position
            int outputPos = k * outputHop;       // write position in output

            // Search window for best source position (skip correlation on first frame).
            int lastFullWindowStart = Math.Max(0, sourceLength - windowSize);
            int searchMin = Math.Clamp(nomSourcePos - searchDelta, 0, lastFullWindowStart);
            int searchMax = Math.Clamp(nomSourcePos + searchDelta, searchMin, lastFullWindowStart);

            int bestPos = k == 0
                ? Math.Min(nomSourcePos, lastFullWindowStart)
                : FindBestSourcePosition(
                    source, sourceOffset, outputBuffer, outputPos,
                    searchMin, searchMax, correlationLength);

            // Overlap-add: window the source grain and accumulate into the output.
            int grainSamples = Math.Min(windowSize, sourceLength - bestPos);
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
        int searchMax,
        int correlationLength)
    {
        int outStart = outputPos - correlationLength;
        int bestPos = searchMin;
        double bestCorr = double.MinValue;

        for (int candidate = searchMin; candidate <= searchMax; candidate++)
        {
            double corr = 0.0;
            for (int n = 0; n < correlationLength; n++)
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
            window[n] = 0.5f - (0.5f * (float)Math.Cos(factor * n));
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
                channels[c][f] = interleaved[(f * channelCount) + c];
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
                result[(f * channelCount) + c] = outputChannels[c][f];
        }

        return result;
    }
}
