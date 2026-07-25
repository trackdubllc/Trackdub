using System.Buffers.Binary;
using Trackdub.Contracts;
using Trackdub.Media.Extraction;

namespace Trackdub.Media.Tests;

public sealed class Pcm16WaveClipExtractorTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Pcm16WaveClipExtractor", Guid.NewGuid().ToString("N"));

    public Pcm16WaveClipExtractorTests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task ExtractAsync_single_range_writes_valid_wave_clip()
    {
        string sourcePath = Path.Combine(tempDirectory, "source-single.wav");
        string destinationPath = Path.Combine(tempDirectory, "single-clip.wav");
        WriteMonoPcm16Wave(sourcePath, sampleRate: 4, [1000, 2000, 3000, 4000, 5000]);
        var extractor = new Pcm16WaveClipExtractor();

        AudioClipExtractionResult result = await extractor.ExtractAsync(
            sourcePath,
            startSeconds: 0.25d,
            endSeconds: 0.75d,
            destinationPath,
            TestContext.Current.CancellationToken);

        WaveData wave = ReadMonoPcm16Wave(destinationPath);
        Assert.Equal(destinationPath, result.OutputPath);
        Assert.Equal(0.5d, result.DurationSeconds);
        Assert.Equal(4, result.SampleRate);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(new short[] { 2000, 3000 }, wave.Samples);
    }

    [Fact]
    public async Task ExtractAsync_discontiguous_ranges_writes_valid_wave_clip_in_range_order()
    {
        string sourcePath = Path.Combine(tempDirectory, "source-multi.wav");
        string destinationPath = Path.Combine(tempDirectory, "multi-clip.wav");
        WriteMonoPcm16Wave(sourcePath, sampleRate: 4, [1000, 2000, 3000, 4000, 5000, 6000]);
        var extractor = new Pcm16WaveClipExtractor();

        AudioClipExtractionResult result = await extractor.ExtractAsync(
            sourcePath,
            [
                new AudioClipRange(0.25d, 0.75d),
                new AudioClipRange(1.00d, 1.50d),
            ],
            destinationPath,
            TestContext.Current.CancellationToken);

        WaveData wave = ReadMonoPcm16Wave(destinationPath);
        Assert.Equal(destinationPath, result.OutputPath);
        Assert.Equal(1.0d, result.DurationSeconds);
        Assert.Equal(4, result.SampleRate);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(new short[] { 2000, 3000, 5000, 6000 }, wave.Samples);
        Assert.Equal(44 + (wave.Samples.Length * sizeof(short)), new FileInfo(destinationPath).Length);
    }

    [Fact]
    public async Task ExtractAsync_invalid_range_reports_offending_range_property()
    {
        string sourcePath = Path.Combine(tempDirectory, "source-invalid-range.wav");
        string destinationPath = Path.Combine(tempDirectory, "invalid-range.wav");
        WriteMonoPcm16Wave(sourcePath, sampleRate: 4, [1000, 2000, 3000, 4000]);
        var extractor = new Pcm16WaveClipExtractor();

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            extractor.ExtractAsync(
                sourcePath,
                [
                    new AudioClipRange(0.25d, 0.75d),
                    new AudioClipRange(1.25d, 1.00d),
                ],
                destinationPath,
                TestContext.Current.CancellationToken));

        Assert.Equal("ranges[1].EndSeconds", exception.ParamName);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void WriteMonoPcm16Wave(string path, int sampleRate, IReadOnlyList<short> samples)
    {
        int dataLength = checked(samples.Count * sizeof(short));
        byte[] header = new byte[44];
        byte[] data = new byte[dataLength];

        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);

        for (int i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * sizeof(short), sizeof(short)), samples[i]);
        }

        using FileStream stream = File.Create(path);
        stream.Write(header);
        stream.Write(data);
    }

    private static WaveData ReadMonoPcm16Wave(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 44);
        Assert.Equal("RIFF"u8.ToArray(), bytes[0..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal("fmt "u8.ToArray(), bytes[12..16]);
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20, 2)));
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2)));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
        Assert.Equal(sampleRate * sizeof(short), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(28, 4)));
        Assert.Equal((short)sizeof(short), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(32, 2)));
        Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34, 2)));
        Assert.Equal("data"u8.ToArray(), bytes[36..40]);

        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        Assert.Equal(44 + dataLength, bytes.Length);
        short[] samples = new short[dataLength / sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (i * sizeof(short)), sizeof(short)));
        }

        return new WaveData(sampleRate, samples);
    }

    private sealed record WaveData(int SampleRate, short[] Samples);
}
