using System;
using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace Trackdub.Inference.Onnx.Audio;

public sealed class MemoryMappedWaveAudioReader : IAudioChannelSamples
{
    private const int TargetChunkSizeBytes = 64 * 1024;
    private readonly MemoryMappedFile mmf;
    private readonly MemoryMappedViewAccessor accessor;
    private readonly FileStream stream;
    private readonly ushort channelCount;
    private readonly ushort blockAlign;
    private readonly long dataStart;

    public int SampleRate { get; }
    public long SampleFrameCount { get; }
    public int ChannelCount => channelCount;

    internal MemoryMappedWaveAudioReader(
        MemoryMappedFile mmf,
        MemoryMappedViewAccessor accessor,
        FileStream stream,
        ushort channelCount,
        ushort blockAlign,
        long dataStart,
        int sampleRate,
        long sampleFrameCount)
    {
        this.mmf = mmf;
        this.accessor = accessor;
        this.stream = stream;
        this.channelCount = channelCount;
        this.blockAlign = blockAlign;
        this.dataStart = dataStart;
        SampleRate = sampleRate;
        SampleFrameCount = sampleFrameCount;
    }

    public void ReadMonoSamples(long startFrame, Span<float> destination)
    {
        if (startFrame < 0 || startFrame >= SampleFrameCount)
        {
            destination.Clear();
            return;
        }

        int framesToRead = (int)Math.Min(destination.Length, SampleFrameCount - startFrame);
        int framesRead = ReadBufferedFrames(startFrame, framesToRead, destination, channelIndex: 0, downmixToMono: true);

        // Zero the remainder of the destination if we hit EOF
        if (framesRead < destination.Length)
        {
            destination.Slice(framesRead).Clear();
        }
    }

    public void ReadChannelSamples(long startFrame, int channelIndex, Span<float> destination)
    {
        if (channelIndex < 0 || channelIndex >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex), "Channel index is outside the WAV channel range.");
        }

        if (startFrame < 0 || startFrame >= SampleFrameCount)
        {
            destination.Clear();
            return;
        }

        int framesToRead = (int)Math.Min(destination.Length, SampleFrameCount - startFrame);
        int framesRead = ReadBufferedFrames(startFrame, framesToRead, destination, channelIndex, downmixToMono: false);

        if (framesRead < destination.Length)
        {
            destination.Slice(framesRead).Clear();
        }
    }

    private int ReadBufferedFrames(long startFrame, int framesToRead, Span<float> destination, int channelIndex, bool downmixToMono)
    {
        if (framesToRead == 0)
        {
            return 0;
        }

        int framesPerChunk = Math.Max(1, TargetChunkSizeBytes / blockAlign);
        int samplesPerChunk = framesPerChunk * channelCount;
        int maxSamplesForRead = (int)Math.Min(samplesPerChunk, (long)framesToRead * channelCount);
        short[] buffer = ArrayPool<short>.Shared.Rent(maxSamplesForRead);

        try
        {
            int destinationOffset = 0;
            while (destinationOffset < framesToRead)
            {
                int chunkFrameCount = Math.Min(framesPerChunk, framesToRead - destinationOffset);
                int chunkSampleCount = chunkFrameCount * channelCount;
                long chunkOffset = dataStart + ((startFrame + destinationOffset) * blockAlign);
                int samplesRead = accessor.ReadArray(chunkOffset, buffer, 0, chunkSampleCount);
                int availableFrameCount = samplesRead / channelCount;
                if (availableFrameCount <= 0)
                {
                    break;
                }

                if (downmixToMono)
                {
                    int sampleOffset = 0;
                    for (int frameIndex = 0; frameIndex < availableFrameCount; frameIndex++)
                    {
                        long sum = 0;
                        for (int channel = 0; channel < channelCount; channel++)
                        {
                            sum += buffer[sampleOffset++];
                        }

                        destination[destinationOffset + frameIndex] = (float)(sum / (channelCount * 32768d));
                    }
                }
                else
                {
                    int sampleOffset = channelIndex;
                    for (int frameIndex = 0; frameIndex < availableFrameCount; frameIndex++)
                    {
                        destination[destinationOffset + frameIndex] = buffer[sampleOffset] / 32768f;
                        sampleOffset += channelCount;
                    }
                }

                destinationOffset += availableFrameCount;
                if (availableFrameCount < chunkFrameCount)
                {
                    break;
                }
            }

            return destinationOffset;
        }
        finally
        {
            ArrayPool<short>.Shared.Return(buffer, clearArray: true);
        }
    }

    public void Dispose()
    {
        accessor.Dispose();
        mmf.Dispose();
        stream.Dispose();
    }
}
