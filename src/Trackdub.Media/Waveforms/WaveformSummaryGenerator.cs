using System.Buffers.Binary;
using Trackdub.Contracts;

namespace Trackdub.Media.Waveforms;

public sealed class WaveformSummaryGenerator : IWaveformSummaryGenerator
{
    private const int DefaultBucketsPerSecond = 10;
    private const int MinimumBucketCount = 128;
    private const int MaximumBucketCount = 12000;

    private readonly int? fixedBucketCount;

    public WaveformSummaryGenerator(int bucketCount = 0)
    {
        if (bucketCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount));
        }

        fixedBucketCount = bucketCount == 0 ? null : bucketCount;
    }

    public async Task<WaveformSummary> GenerateAsync(string audioPath, CancellationToken cancellationToken)
    {
        string fullAudioPath = Path.GetFullPath(audioPath);
        if (!File.Exists(fullAudioPath))
        {
            throw new FileNotFoundException("Audio file was not found for waveform generation.", fullAudioPath);
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(fullAudioPath, cancellationToken).ConfigureAwait(false);
        int resolvedBucketCount = fixedBucketCount ?? ResolveBucketCount(waveInfo.DurationSeconds);
        float[] peaks = new float[resolvedBucketCount];
        long framesPerBucket = Math.Max(1, (long)Math.Ceiling((double)waveInfo.SampleFrames / resolvedBucketCount));
        int sampleStride = waveInfo.BlockAlign / waveInfo.ChannelCount;
        if (waveInfo.BlockAlign % waveInfo.ChannelCount != 0 || sampleStride < sizeof(short))
        {
            throw new InvalidOperationException("WAV block alignment is incompatible with 16-bit sample decoding.");
        }

        int framesPerRead = Math.Max(1, 4096 / Math.Max(1, waveInfo.BlockAlign));
        byte[] buffer = new byte[framesPerRead * waveInfo.BlockAlign];

        await using var stream = new FileStream(
            fullAudioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = waveInfo.DataStartPosition;

        long frameIndex = 0;
        while (frameIndex < waveInfo.SampleFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int framesRemaining = (int)Math.Min(framesPerRead, waveInfo.SampleFrames - frameIndex);
            int bytesToRead = framesRemaining * waveInfo.BlockAlign;
            int bytesRead = await ReadAtLeastAsync(stream, buffer, bytesToRead, cancellationToken).ConfigureAwait(false);
            if (bytesRead != bytesToRead)
            {
                throw new InvalidOperationException("WAV payload ended before the declared sample data was fully read.");
            }

            ReadOnlySpan<byte> span = buffer.AsSpan(0, bytesRead);
            int completeFrameBytes = bytesRead - (bytesRead % waveInfo.BlockAlign);
            for (int offset = 0; offset < completeFrameBytes; offset += waveInfo.BlockAlign)
            {
                float framePeak = 0f;
                for (int channel = 0; channel < waveInfo.ChannelCount; channel++)
                {
                    int sampleOffset = offset + (channel * sampleStride);
                    short sample = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(sampleOffset, sizeof(short)));
                    float magnitude = Math.Abs(sample / 32768f);
                    if (magnitude > framePeak)
                    {
                        framePeak = magnitude;
                    }
                }

                int bucketIndex = (int)Math.Min(resolvedBucketCount - 1, frameIndex / framesPerBucket);
                if (framePeak > peaks[bucketIndex])
                {
                    peaks[bucketIndex] = framePeak;
                }

                frameIndex++;
            }
        }

        return new WaveformSummary(
            resolvedBucketCount,
            waveInfo.SampleRate,
            waveInfo.ChannelCount,
            waveInfo.DurationSeconds,
            peaks);
    }

    private static int ResolveBucketCount(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            return MinimumBucketCount;
        }

        int durationBucketCount = (int)Math.Ceiling(durationSeconds * DefaultBucketsPerSecond);
        return Math.Clamp(durationBucketCount, MinimumBucketCount, MaximumBucketCount);
    }

    private static async Task<int> ReadAtLeastAsync(
        FileStream stream,
        byte[] buffer,
        int bytesToRead,
        CancellationToken cancellationToken)
    {
        int totalBytesRead = 0;
        while (totalBytesRead < bytesToRead)
        {
            int bytesRead = await stream.ReadAsync(
                buffer.AsMemory(totalBytesRead, bytesToRead - totalBytesRead),
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }
}
