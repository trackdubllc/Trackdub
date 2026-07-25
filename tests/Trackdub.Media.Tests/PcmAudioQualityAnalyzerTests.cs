using Trackdub.Contracts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Media.Quality;

namespace Trackdub.Media.Tests;

public sealed class PcmAudioQualityAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_detects_low_volume_fixture()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(tempDirectory, "quiet.wav");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            await WriteWaveAsync(path, sample => 0.005d * Math.Sin(2d * Math.PI * 220d * sample / 48000d));
            var analyzer = new PcmAudioQualityAnalyzer();
            string requestedPath = Path.Combine(tempDirectory, ".", "quiet.wav");

            AudioQualityAnalysisResult result = await analyzer.AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    requestedPath,
                    SpeechAudioSourceKind.FullMix,
                    AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.FullMix)),
                TestContext.Current.CancellationToken);

            Assert.Equal(requestedPath, result.AudioPath);
            Assert.Contains(AudioQualityDefectKind.LowVolume, result.TriggeredDefects);
            Assert.True(result.Metrics.ActiveRmsDbfs < -32.0d);
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
    public async Task AnalyzeAsync_reports_unavailable_snr_without_quiet_floor()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(tempDirectory, "music-bed.wav");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            await WriteWaveAsync(path, sample =>
                0.18d * Math.Sin(2d * Math.PI * 220d * sample / 48000d) +
                0.08d * Math.Sin(2d * Math.PI * 7000d * sample / 48000d));
            var analyzer = new PcmAudioQualityAnalyzer();

            AudioQualityAnalysisResult result = await analyzer.AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    path,
                    SpeechAudioSourceKind.FullMix,
                    AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.FullMix)),
                TestContext.Current.CancellationToken);

            Assert.Equal(AudioSnrConfidence.Unavailable, result.Metrics.SnrConfidence);
            Assert.Null(result.Metrics.SnrDb);
            Assert.Contains(result.Warnings, warning => warning.Contains("SNR unavailable", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task WriteWaveAsync(string outputPath, Func<int, double> sampleFactory)
    {
        short[] samples = new short[48000];
        for (int index = 0; index < samples.Length; index++)
        {
            double value = Math.Clamp(sampleFactory(index), -1d, 1d);
            samples[index] = (short)Math.Round(value * short.MaxValue);
        }

        await using FileStream stream = new(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        await using var writer = new BinaryWriter(stream);
        int dataSize = samples.Length * sizeof(short);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(48000);
        writer.Write(48000 * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        foreach (short sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
