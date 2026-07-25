using System.Buffers.Binary;
using Trackdub.Contracts;

namespace Trackdub.Media.Waveforms;

public sealed class Pcm16ReferenceClipAnalyzer : IReferenceClipAnalyzer
{
    private const double ActiveSpeechAmplitudeThreshold = 0.015d;
    private const int AnalysisWindowMilliseconds = 20;

    public async Task<ReferenceClipAnalysis> AnalyzeAsync(
        string wavePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavePath);
        string fullPath = Path.GetFullPath(wavePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Reference clip was not found.", fullPath);
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(fullPath, cancellationToken).ConfigureAwait(false);
        long activeFrames = await CountActiveFramesAsync(fullPath, waveInfo, cancellationToken).ConfigureAwait(false);
        double activeSpeechSeconds = waveInfo.SampleRate <= 0
            ? 0d
            : activeFrames / (double)waveInfo.SampleRate;

        return new ReferenceClipAnalysis(
            waveInfo.DurationSeconds,
            activeSpeechSeconds,
            waveInfo.SampleRate,
            waveInfo.ChannelCount);
    }

    private static async Task<long> CountActiveFramesAsync(
        string fullPath,
        WavePcm16Info waveInfo,
        CancellationToken cancellationToken)
    {
        int framesPerWindow = Math.Max(1, waveInfo.SampleRate * AnalysisWindowMilliseconds / 1000);
        int bytesPerSample = waveInfo.BitsPerSample / 8;
        byte[] buffer = new byte[Math.Max(waveInfo.BlockAlign, framesPerWindow * waveInfo.BlockAlign)];
        long activeFrames = 0;
        int windowFrameCount = 0;
        double windowAbsoluteSum = 0d;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = waveInfo.DataStartPosition;

        long remainingBytes = waveInfo.DataLengthBytes;
        while (remainingBytes > 0)
        {
            int bytesToRead = (int)Math.Min(buffer.Length, remainingBytes);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new InvalidOperationException("WAV payload ended before the declared sample data was fully read.");
            }

            remainingBytes -= bytesRead;
            int completeFrameBytes = bytesRead - (bytesRead % waveInfo.BlockAlign);
            for (int frameOffset = 0; frameOffset < completeFrameBytes; frameOffset += waveInfo.BlockAlign)
            {
                double framePeak = 0d;
                for (int channel = 0; channel < waveInfo.ChannelCount; channel++)
                {
                    int sampleOffset = frameOffset + channel * bytesPerSample;
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(sampleOffset, bytesPerSample));
                    framePeak = Math.Max(framePeak, Math.Abs(sample / 32768d));
                }

                windowAbsoluteSum += framePeak;
                windowFrameCount++;
                if (windowFrameCount >= framesPerWindow)
                {
                    activeFrames += ResolveActiveFrames(windowAbsoluteSum, windowFrameCount);
                    windowAbsoluteSum = 0d;
                    windowFrameCount = 0;
                }
            }
        }

        if (windowFrameCount > 0)
        {
            activeFrames += ResolveActiveFrames(windowAbsoluteSum, windowFrameCount);
        }

        return activeFrames;
    }

    private static int ResolveActiveFrames(double windowAbsoluteSum, int windowFrameCount)
    {
        double averageAmplitude = windowFrameCount <= 0 ? 0d : windowAbsoluteSum / windowFrameCount;
        return averageAmplitude >= ActiveSpeechAmplitudeThreshold ? windowFrameCount : 0;
    }
}
