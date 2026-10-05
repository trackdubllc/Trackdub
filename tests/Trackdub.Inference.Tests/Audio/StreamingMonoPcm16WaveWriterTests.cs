using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Tests.Audio;

public sealed class StreamingMonoPcm16WaveWriterTests
{
    [Fact]
    public async Task WritesBoundedBlocksWithTheExistingPcmEncodingAndHeader()
    {
        float[] samples = Enumerable.Range(0, 20000)
            .Select(static i => (i % 17 - 8) / 7f).ToArray();
        await using var stream = new BoundedWriteStream();
        var writer = await StreamingMonoPcm16WaveWriter.CreateAsync(
            stream, samples.Length, 48000, CancellationToken.None);
        await writer.WriteAsync(samples.AsMemory(0, 13), CancellationToken.None);
        await writer.WriteAsync(samples.AsMemory(13), CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);

        Assert.Equal(samples.Length, writer.SampleFramesWritten);
        Assert.Equal(WaveAudioWriter.EncodeMonoPcm16(samples, 48000), stream.ToArray());
        Assert.True(stream.WriteCalls > 2);
    }

    [Fact]
    public async Task RejectsIncompleteOrExcessOutput()
    {
        await using var stream = new MemoryStream();
        var writer = await StreamingMonoPcm16WaveWriter.CreateAsync(
            stream, 2, 48000, CancellationToken.None);
        await writer.WriteAsync(new float[1], CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteAsync(new float[2], CancellationToken.None));
        Assert.Equal(46, stream.Length);
    }

    [Fact]
    public async Task RejectsUnrepresentableWavLengthBeforeWritingHeader()
    {
        await using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            StreamingMonoPcm16WaveWriter.CreateAsync(stream, int.MaxValue, 48000, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task EmptyOutputIsAValidHeaderAndCancellationStopsWrites()
    {
        await using var stream = new MemoryStream();
        var writer = await StreamingMonoPcm16WaveWriter.CreateAsync(
            stream, 0, 48000, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
        Assert.Equal(WaveAudioWriter.EncodeMonoPcm16([], 48000), stream.ToArray());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync(ReadOnlyMemory<float>.Empty, cts.Token));
    }

    private sealed class BoundedWriteStream : MemoryStream
    {
        public int WriteCalls { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.InRange(buffer.Length, 1, 8192);
            WriteCalls++;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
