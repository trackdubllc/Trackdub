using Trackdub.Contracts;

namespace Trackdub.Media.Waveforms;

public sealed class Pcm16ReferenceClipTrimmer : IReferenceClipTrimmer
{
    private const float ActiveSpeechAmplitudeThreshold = 0.015f;
    private const double ActiveEdgeInsetSeconds = 0.05d;

    public async Task<ReferenceClipTrimResult> TrimAsync(
        string wavePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        string fullPath = Path.GetFullPath(wavePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Reference clip was not found.", fullPath);
        }

        WavePcm16Samples samples = await WavePcm16
            .ReadAllSamplesAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (samples.FrameCount == 0)
        {
            return new ReferenceClipTrimResult(false, 0d, 0d, 0d, 0d);
        }

        (int firstActiveFrame, int lastActiveFrame) = FindActiveFrameRange(samples);
        double originalDurationSeconds = samples.FrameCount / (double)samples.SampleRate;
        if (firstActiveFrame < 0)
        {
            return new ReferenceClipTrimResult(
                false,
                originalDurationSeconds,
                originalDurationSeconds,
                0d,
                0d);
        }

        // Only apply inset when there is actual edge silence to remove
        bool hasEdgeSilence = firstActiveFrame > 0 || lastActiveFrame < samples.FrameCount - 1;
        int startFrame = firstActiveFrame;
        int endFrameExclusive = lastActiveFrame + 1;

        if (hasEdgeSilence)
        {
            int activeFrameCount = lastActiveFrame - firstActiveFrame + 1;
            int requestedInsetFrames = Math.Max(1, (int)Math.Round(samples.SampleRate * ActiveEdgeInsetSeconds));
            int insetFrames = Math.Min(requestedInsetFrames, Math.Max(0, (activeFrameCount - 1) / 4));
            startFrame = firstActiveFrame + insetFrames;
            endFrameExclusive = lastActiveFrame + 1 - insetFrames;
            if (startFrame >= endFrameExclusive)
            {
                startFrame = firstActiveFrame;
                endFrameExclusive = lastActiveFrame + 1;
            }
        }

        if (startFrame == 0 && endFrameExclusive == samples.FrameCount)
        {
            return new ReferenceClipTrimResult(
                false,
                originalDurationSeconds,
                originalDurationSeconds,
                0d,
                0d);
        }

        int outputFrameCount = endFrameExclusive - startFrame;
        int outputSampleCount = checked(outputFrameCount * samples.ChannelCount);
        var trimmedSamples = new float[outputSampleCount];
        Array.Copy(
            samples.Samples,
            startFrame * samples.ChannelCount,
            trimmedSamples,
            0,
            outputSampleCount);

        string? directoryPath = Path.GetDirectoryName(fullPath);
        string trimPath = Path.Combine(
            directoryPath ?? string.Empty,
            Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".trimmed");
        try
        {
            await WavePcm16
                .WriteSamplesAsync(trimPath, trimmedSamples, samples.SampleRate, samples.ChannelCount, cancellationToken)
                .ConfigureAwait(false);

            if (File.Exists(fullPath))
            {
                File.Replace(trimPath, fullPath, null);
            }
            else
            {
                File.Move(trimPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(trimPath))
            {
                File.Delete(trimPath);
            }
        }

        double trimmedDurationSeconds = outputFrameCount / (double)samples.SampleRate;
        return new ReferenceClipTrimResult(
            true,
            originalDurationSeconds,
            trimmedDurationSeconds,
            startFrame / (double)samples.SampleRate,
            (samples.FrameCount - endFrameExclusive) / (double)samples.SampleRate);
    }

    private static (int FirstActiveFrame, int LastActiveFrame) FindActiveFrameRange(WavePcm16Samples samples)
    {
        int firstActiveFrame = -1;
        int lastActiveFrame = -1;
        for (int frame = 0; frame < samples.FrameCount; frame++)
        {
            if (!IsActiveFrame(samples, frame))
            {
                continue;
            }

            firstActiveFrame = frame;
            break;
        }

        if (firstActiveFrame < 0)
        {
            return (-1, -1);
        }

        for (int frame = samples.FrameCount - 1; frame >= firstActiveFrame; frame--)
        {
            if (!IsActiveFrame(samples, frame))
            {
                continue;
            }

            lastActiveFrame = frame;
            break;
        }

        return (firstActiveFrame, lastActiveFrame);
    }

    private static bool IsActiveFrame(WavePcm16Samples samples, int frame)
    {
        int offset = frame * samples.ChannelCount;
        for (int channel = 0; channel < samples.ChannelCount; channel++)
        {
            if (Math.Abs(samples.Samples[offset + channel]) >= ActiveSpeechAmplitudeThreshold)
            {
                return true;
            }
        }

        return false;
    }
}
