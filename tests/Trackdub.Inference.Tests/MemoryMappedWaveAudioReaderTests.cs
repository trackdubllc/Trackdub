using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Tests;

public sealed class MemoryMappedWaveAudioReaderTests
{
    [Fact]
    public async Task ReadMonoSamples_AveragesInterleavedSamplesAcrossBufferedChunks()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "buffered-mono.wav");
            const short channelCount = 3;
            const int sampleRate = 16000;
            const int frameCount = 20000;
            short[] interleaved = new short[frameCount * channelCount];
            var expected = new float[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                short first = (short)((frame % 1024) - 512);
                short second = (short)(256 - (frame % 512));
                short third = (short)((frame % 128) * 4 - 256);

                int sampleIndex = frame * channelCount;
                interleaved[sampleIndex] = first;
                interleaved[sampleIndex + 1] = second;
                interleaved[sampleIndex + 2] = third;
                expected[frame] = (float)((first + second + third) / (channelCount * 32768d));
            }

            WriteWaveFile(path, sampleRate, channelCount, interleaved);

            using IAudioSamples reader = await WaveAudioReader.ReadMonoPcm16Async(path, CancellationToken.None);
            var actual = new float[frameCount];

            reader.ReadMonoSamples(0, actual);

            AssertSamplesEqual(expected, actual, tolerance: 1e-6f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadChannelSamples_ReturnsRequestedChannelAndClearsRemainderAfterEof()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "channel.wav");
            const short channelCount = 2;
            const int sampleRate = 44100;
            short[] interleaved = new short[]
            {
                1000, -1000,
                2000, -2000,
                3000, -3000,
                4000, -4000
            };

            WriteWaveFile(path, sampleRate, channelCount, interleaved);

            using IAudioChannelSamples reader = await WaveAudioReader.ReadPcm16Async(path, CancellationToken.None);
            float[] actual = new float[] { 123f, 123f, 123f };

            reader.ReadChannelSamples(startFrame: 2, channelIndex: 1, actual);

            float[] expected = new float[]
            {
                -3000 / 32768f,
                -4000 / 32768f,
                0f
            };

            AssertSamplesEqual(expected, actual, tolerance: 1e-6f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadMonoSamples_WhenMappedDataEndsEarly_TreatsShortReadAsEofAndClearsRemainder()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "truncated.wav");
            const short channelCount = 2;
            const int sampleRate = 16000;
            short[] interleaved = new short[]
            {
                1200, -1200,
                2400, -2400
            };

            WriteWaveFile(path, sampleRate, channelCount, interleaved);

            FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);
            MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            using var reader = new MemoryMappedWaveAudioReader(
                mmf,
                accessor,
                stream,
                (ushort)channelCount,
                blockAlign: (ushort)(channelCount * sizeof(short)),
                dataStart: 44,
                sampleRate,
                sampleFrameCount: 4);

            float[] actual = new float[] { 999f, 999f, 999f, 999f };

            reader.ReadMonoSamples(0, actual);

            float[] expected = new float[]
            {
                0f,
                0f,
                0f,
                0f
            };

            AssertSamplesEqual(expected, actual, tolerance: 1e-6f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadChannelSamples_WhenMappedDataEndsMidFrame_TreatsPartialFrameAsEofAndClearsRemainder()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "partial-frame.pcm");
            byte[] payload = new byte[3 * sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(0, 2), 1200);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2, 2), -1200);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4, 2), 2400);
            File.WriteAllBytes(path, payload);

            FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
                stream,
                null,
                0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);
            MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, stream.Length, MemoryMappedFileAccess.Read);
            using var reader = new MemoryMappedWaveAudioReader(
                mmf,
                accessor,
                stream,
                channelCount: 2,
                blockAlign: 2 * sizeof(short),
                dataStart: 0,
                sampleRate: 16000,
                sampleFrameCount: 2);

            float[] actual = new float[] { 999f, 999f };

            reader.ReadChannelSamples(0, channelIndex: 0, actual);

            float[] expected = new float[]
            {
                1200 / 32768f,
                0f
            };

            AssertSamplesEqual(expected, actual, tolerance: 1e-6f);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadPcm16Async_WhenBlockAlignDoesNotMatchPcm16FrameSize_RejectsFile()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "invalid-block-align.wav");
            const short channelCount = 2;
            const int sampleRate = 16000;
            short[] interleaved = new short[]
            {
                1000, -1000,
                2000, -2000
            };

            WriteWaveFile(path, sampleRate, channelCount, interleaved, blockAlignOverride: 6);

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaveAudioReader.ReadPcm16Async(path, CancellationToken.None));

            Assert.Contains("block alignment 6", exception.Message, StringComparison.Ordinal);
            Assert.Contains("expected 4", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"trackdub-memorymapped-wave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteWaveFile(
        string path,
        int sampleRate,
        short channelCount,
        short[] interleavedSamples,
        int? blockAlignOverride = null)
    {
        const short bitsPerSample = 16;
        int blockAlign = blockAlignOverride ?? channelCount * (bitsPerSample / 8);
        int dataLength = interleavedSamples.Length * sizeof(short);
        byte[] buffer = new byte[44 + dataLength];

        "RIFF"u8.CopyTo(buffer.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), 36 + dataLength);
        "WAVE"u8.CopyTo(buffer.AsSpan(8, 4));
        "fmt "u8.CopyTo(buffer.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22, 2), channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28, 4), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(buffer.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40, 4), dataLength);
        Buffer.BlockCopy(interleavedSamples, 0, buffer, 44, dataLength);

        File.WriteAllBytes(path, buffer);
    }

    private static void AssertSamplesEqual(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.InRange(actual[index], expected[index] - tolerance, expected[index] + tolerance);
        }
    }
}
