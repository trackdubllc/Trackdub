using System;

namespace Trackdub.Inference.Onnx.Audio;

public sealed class ResampledAudioReader : IAudioSamples
{
    private readonly IAudioSamples source;
    private readonly int targetSampleRate;
    private readonly double ratio;

    public int SampleRate => targetSampleRate;
    public long SampleFrameCount { get; }

    internal ResampledAudioReader(IAudioSamples source, int targetSampleRate)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.targetSampleRate = targetSampleRate;
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate), "Sample rate must be positive.");
        }

        ratio = (double)source.SampleRate / targetSampleRate;
        SampleFrameCount = source.SampleFrameCount == 0
            ? 0
            : Math.Max(1, (long)Math.Round(source.SampleFrameCount / ratio));
    }

    public void ReadMonoSamples(long startFrame, Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        // Match IAudioSamples contract used by MemoryMappedWaveAudioReader:
        // an out-of-range startFrame zero-fills the destination instead of throwing,
        // so callers can safely request reads at or past EOF as silence.
        if (startFrame < 0 || startFrame >= SampleFrameCount)
        {
            destination.Clear();
            return;
        }

        int framesToCompute = (int)Math.Min(destination.Length, SampleFrameCount - startFrame);

        // Find the source boundaries needed for this destination chunk
        double startSourcePosition = startFrame * ratio;
        long startSourceLeftIndex = (long)Math.Floor(startSourcePosition);

        double endSourcePosition = (startFrame + framesToCompute - 1) * ratio;
        long endSourceRightIndex = Math.Min((long)Math.Floor(endSourcePosition) + 1, source.SampleFrameCount - 1);

        int sourceFramesNeeded = (int)(endSourceRightIndex - startSourceLeftIndex + 1);

        // Rent a buffer to hold the source chunk
        float[] sourceBuffer = System.Buffers.ArrayPool<float>.Shared.Rent(sourceFramesNeeded);
        try
        {
            Span<float> sourceSpan = sourceBuffer.AsSpan(0, sourceFramesNeeded);
            source.ReadMonoSamples(startSourceLeftIndex, sourceSpan);

            double sourcePosition = startSourcePosition;
            for (int i = 0; i < framesToCompute; i++, sourcePosition += ratio)
            {
                long globalLeftIndex = (long)Math.Floor(sourcePosition);
                long globalRightIndex = Math.Min(globalLeftIndex + 1, source.SampleFrameCount - 1);
                double fraction = sourcePosition - globalLeftIndex;

                int localLeftIndex = (int)(globalLeftIndex - startSourceLeftIndex);
                int localRightIndex = (int)(globalRightIndex - startSourceLeftIndex);

                float left = sourceSpan[localLeftIndex];
                float right = sourceSpan[localRightIndex];
                destination[i] = (float)(left + ((right - left) * fraction));
            }

            if (framesToCompute < destination.Length)
            {
                destination.Slice(framesToCompute).Clear();
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(sourceBuffer);
        }
    }

    public void Dispose()
    {
        source.Dispose();
    }
}

internal sealed class ResampledChannelAudioReader : IAudioChannelSamples
{
    private readonly IAudioChannelSamples source;
    private readonly int targetSampleRate;
    private readonly double ratio;

    public int SampleRate => targetSampleRate;
    public int ChannelCount => source.ChannelCount;
    public long SampleFrameCount { get; }

    internal ResampledChannelAudioReader(IAudioChannelSamples source, int targetSampleRate)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.targetSampleRate = targetSampleRate;
        if (targetSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSampleRate), "Sample rate must be positive.");
        }

        ratio = (double)source.SampleRate / targetSampleRate;
        SampleFrameCount = source.SampleFrameCount == 0
            ? 0
            : Math.Max(1, (long)Math.Round(source.SampleFrameCount / ratio));
    }

    public void ReadMonoSamples(long startFrame, Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (ChannelCount == 1)
        {
            ReadChannelSamples(startFrame, 0, destination);
            return;
        }

        float[] channelBuffer = System.Buffers.ArrayPool<float>.Shared.Rent(destination.Length);
        try
        {
            destination.Clear();
            Span<float> channelSpan = channelBuffer.AsSpan(0, destination.Length);
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                channelSpan.Clear();
                ReadChannelSamples(startFrame, channel, channelSpan);
                for (int index = 0; index < destination.Length; index++)
                {
                    destination[index] += channelSpan[index] / ChannelCount;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(channelBuffer);
        }
    }

    public void ReadChannelSamples(long startFrame, int channelIndex, Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        if (channelIndex < 0 || channelIndex >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channelIndex), "Channel index is outside the source channel range.");
        }

        if (startFrame < 0 || startFrame >= SampleFrameCount)
        {
            destination.Clear();
            return;
        }

        int framesToCompute = (int)Math.Min(destination.Length, SampleFrameCount - startFrame);
        double startSourcePosition = startFrame * ratio;
        long startSourceLeftIndex = (long)Math.Floor(startSourcePosition);
        double endSourcePosition = (startFrame + framesToCompute - 1) * ratio;
        long endSourceRightIndex = Math.Min((long)Math.Floor(endSourcePosition) + 1, source.SampleFrameCount - 1);
        int sourceFramesNeeded = (int)(endSourceRightIndex - startSourceLeftIndex + 1);

        float[] sourceBuffer = System.Buffers.ArrayPool<float>.Shared.Rent(sourceFramesNeeded);
        try
        {
            Span<float> sourceSpan = sourceBuffer.AsSpan(0, sourceFramesNeeded);
            source.ReadChannelSamples(startSourceLeftIndex, channelIndex, sourceSpan);

            double sourcePosition = startSourcePosition;
            for (int index = 0; index < framesToCompute; index++, sourcePosition += ratio)
            {
                long globalLeftIndex = (long)Math.Floor(sourcePosition);
                long globalRightIndex = Math.Min(globalLeftIndex + 1, source.SampleFrameCount - 1);
                double fraction = sourcePosition - globalLeftIndex;

                int localLeftIndex = (int)(globalLeftIndex - startSourceLeftIndex);
                int localRightIndex = (int)(globalRightIndex - startSourceLeftIndex);

                float left = sourceSpan[localLeftIndex];
                float right = sourceSpan[localRightIndex];
                destination[index] = (float)(left + ((right - left) * fraction));
            }

            if (framesToCompute < destination.Length)
            {
                destination.Slice(framesToCompute).Clear();
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(sourceBuffer);
        }
    }

    public void Dispose()
    {
        source.Dispose();
    }
}

internal static class AudioResampler
{
    public static float[] Resample(float[] input, int inputSampleRate, int outputSampleRate)
    {
        // Legacy array-based fallback (if still needed)
        ArgumentNullException.ThrowIfNull(input);
        if (inputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputSampleRate), "Sample rate must be positive.");
        if (outputSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputSampleRate), "Sample rate must be positive.");
        if (input.Length == 0 || inputSampleRate == outputSampleRate) return input.ToArray();

        int outputLength = Math.Max(1, (int)Math.Round(input.Length * (double)outputSampleRate / inputSampleRate));
        var output = new float[outputLength];
        double ratio = (double)inputSampleRate / outputSampleRate;

        double sourcePosition = 0;
        for (int index = 0; index < outputLength; index++, sourcePosition += ratio)
        {
            int leftIndex = (int)Math.Floor(sourcePosition);
            int rightIndex = Math.Min(leftIndex + 1, input.Length - 1);
            double fraction = sourcePosition - leftIndex;

            float left = input[Math.Min(leftIndex, input.Length - 1)];
            float right = input[rightIndex];
            output[index] = (float)(left + ((right - left) * fraction));
        }

        return output;
    }

    public static IAudioSamples CreateResampledStream(IAudioSamples source, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Ownership is transferred to the returned stream. It may be the original
        // source when rates already match, or a wrapper that disposes the source.
        if (source.SampleRate == targetSampleRate)
        {
            return source;
        }

        return new ResampledAudioReader(source, targetSampleRate);
    }

    public static IAudioChannelSamples CreateResampledChannelStream(IAudioChannelSamples source, int targetSampleRate)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.SampleRate == targetSampleRate)
        {
            return source;
        }

        return new ResampledChannelAudioReader(source, targetSampleRate);
    }
}
