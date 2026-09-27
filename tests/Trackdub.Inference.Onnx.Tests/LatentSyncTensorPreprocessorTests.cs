using Trackdub.Inference.Onnx.LipSynthesis;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class LatentSyncTensorPreprocessorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(320, 1)]
    [InlineData(321, 2)]
    [InlineData(480_000, 1_500)]
    [InlineData(480_001, 1_501)]
    public void GetWhisperFeatureFrameCount_uses_50_hz_ceil_alignment(int sampleCount, int expectedFrames)
    {
        Assert.Equal(expectedFrames, LatentSyncTensorPreprocessor.GetWhisperFeatureFrameCount(sampleCount));
    }

    [Fact]
    public void ComputeWhisperMelSpectrogram_rejects_input_longer_than_one_encoder_window()
    {
        float[] audio = new float[LatentSyncTensorPreprocessor.WhisperWindowSamples + 1];

        Assert.Throws<ArgumentException>(() => LatentSyncTensorPreprocessor.ComputeWhisperMelSpectrogram(audio));
    }
}
