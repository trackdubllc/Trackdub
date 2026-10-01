using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

public sealed class SpleeterInputLayoutTests
{
    [Fact]
    public void ResolveBlockDimensions_ChannelsFirstExport_PutsTwoChannelsBeforeTheSingleSplit()
    {
        // csukuangfj/sherpa-onnx-spleeter-2stems @ 7001ba3: x = [2, num_splits, 512, 1024]
        Assert.Equal([2, 1, 512, 1024], SpleeterOnnxSeparator.ResolveBlockDimensions([2, -1, 512, 1024]));
    }

    [Fact]
    public void ResolveBlockDimensions_SplitsFirstExport_PutsTheSingleSplitBeforeTwoChannels()
    {
        // csukuangfj/sherpa-onnx-spleeter-2stems @ 3e5a4dd: x = [num_splits, 2, 512, 1024]
        Assert.Equal([1, 2, 512, 1024], SpleeterOnnxSeparator.ResolveBlockDimensions([-1, 2, 512, 1024]));
    }

    [Fact]
    public void ResolveBlockDimensions_AcceptsFullyDynamicTrailingAxes()
    {
        Assert.Equal([1, 2, 512, 1024], SpleeterOnnxSeparator.ResolveBlockDimensions([-1, 2, -1, -1]));
    }

    [Theory]
    [InlineData(new[] { -1, -1, 512, 1024 })]
    [InlineData(new[] { 2, 2, 512, 1024 })]
    [InlineData(new[] { 3, 2, 512, 1024 })]
    [InlineData(new[] { 2, 3, 512, 1024 })]
    [InlineData(new[] { 1, 1, 512, 1024 })]
    [InlineData(new[] { -1, 2, 256, 1024 })]
    [InlineData(new[] { -1, 2, 512, 2048 })]
    [InlineData(new[] { 2, 512, 1024 })]
    [InlineData(new int[] { })]
    public void ResolveBlockDimensions_RejectsLayoutsItCannotMapSafely(int[] declared)
    {
        Assert.Throws<InvalidOperationException>(() => SpleeterOnnxSeparator.ResolveBlockDimensions(declared));
    }

    [Fact]
    public void ResolveBlockDimensions_BothLayoutsShareOneMemoryOrder()
    {
        // With a single split the two layouts differ only in how the same [channel, frame, freq]
        // buffer is labelled, which is why one STFT block serves both exports.
        int[] channelsFirst = SpleeterOnnxSeparator.ResolveBlockDimensions([2, -1, 512, 1024]);
        int[] splitsFirst = SpleeterOnnxSeparator.ResolveBlockDimensions([-1, 2, 512, 1024]);

        Assert.Equal(channelsFirst.Aggregate(1, (a, b) => a * b), splitsFirst.Aggregate(1, (a, b) => a * b));
    }
}
