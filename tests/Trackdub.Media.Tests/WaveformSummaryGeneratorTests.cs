using System.Text;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class WaveformSummaryGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_uses_duration_based_bucket_count_by_default()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            string audioPath = Path.Combine(tempDirectory, "sample.wav");
            WritePcm16MonoWave(audioPath, sampleRate: 1000, durationSeconds: 20);

            var generator = new WaveformSummaryGenerator();
            var waveform = await generator.GenerateAsync(audioPath, TestContext.Current.CancellationToken);

            Assert.True(waveform.BucketCount > 128);
            Assert.Equal(waveform.BucketCount, waveform.Peaks.Count);
            Assert.Contains(waveform.Peaks, static peak => peak > 0f);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GenerateAsync_preserves_explicit_bucket_count()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            string audioPath = Path.Combine(tempDirectory, "sample.wav");
            WritePcm16MonoWave(audioPath, sampleRate: 1000, durationSeconds: 20);

            var generator = new WaveformSummaryGenerator(bucketCount: 16);
            var waveform = await generator.GenerateAsync(audioPath, TestContext.Current.CancellationToken);

            Assert.Equal(16, waveform.BucketCount);
            Assert.Equal(16, waveform.Peaks.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void WritePcm16MonoWave(string path, int sampleRate, int durationSeconds)
    {
        int sampleCount = sampleRate * durationSeconds;
        int dataLength = sampleCount * sizeof(short);

        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (int index = 0; index < sampleCount; index++)
        {
            writer.Write((short)(index % 2 == 0 ? short.MaxValue / 2 : 0));
        }
    }
}
