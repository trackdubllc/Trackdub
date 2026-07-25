using System;

namespace Trackdub.Inference.Onnx.Audio;

public interface IAudioSamples : IDisposable
{
    int SampleRate { get; }
    long SampleFrameCount { get; }
    void ReadMonoSamples(long startFrame, Span<float> destination);
}

internal interface IAudioChannelSamples : IAudioSamples
{
    int ChannelCount { get; }
    void ReadChannelSamples(long startFrame, int channelIndex, Span<float> destination);
}
