using Trackdub.Contracts;
using Trackdub.Media.Enhancement;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class FfmpegSpeechAudioEnhancementServiceTests
{
    [Fact]
    public void BuildArguments_uses_expected_speech_cleanup_filter_and_pcm_output()
    {
        IReadOnlyList<string> arguments = FfmpegSpeechAudioEnhancementCommandBuilder.BuildArguments(
            "input.wav",
            "output.wav");

        int filterFlagIndex = arguments
            .Select((value, index) => (value, index))
            .Where(static item => item.value == "-filter:a")
            .Select(static item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        Assert.True(filterFlagIndex >= 0);
        Assert.Equal(
            "highpass=f=80,lowpass=f=8000,afftdn=nr=8:nf=-55,speechnorm=e=6.25:l=1",
            arguments[filterFlagIndex + 1]);
        Assert.Contains("pcm_s16le", arguments);
        Assert.Contains("48000", arguments);
    }

    [RequiresFfmpegFact]
    public async Task EnhanceAsync_creates_mono_wav_output()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string sourcePath = Path.Combine(tempDirectory, "input.wav");
            string outputPath = Path.Combine(tempDirectory, "enhanced.wav");
            Directory.CreateDirectory(tempDirectory);
            await CreateSpeechLikeFixtureAsync(sourcePath).ConfigureAwait(false);

            var service = new FfmpegSpeechAudioEnhancementService();
            SpeechAudioEnhancementResult result = await service.EnhanceAsync(
                new SpeechAudioEnhancementRequest(sourcePath, outputPath),
                TestContext.Current.CancellationToken).ConfigureAwait(false);

            WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(outputPath, TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(outputPath, result.OutputPath);
            Assert.Equal(48000, result.SampleRate);
            Assert.Equal(1, result.ChannelCount);
            Assert.True(result.DurationSeconds > 0.9d);
            Assert.Equal(48000, waveInfo.SampleRate);
            Assert.Equal(1, waveInfo.ChannelCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task CreateSpeechLikeFixtureAsync(string outputPath)
    {
        short[] samples = new short[48000];
        for (int i = 0; i < samples.Length; i++)
        {
            double time = i / 48000d;
            double carrier = Math.Sin(2d * Math.PI * 220d * time);
            double overtone = 0.45d * Math.Sin(2d * Math.PI * 440d * time);
            double hiss = 0.08d * Math.Sin(2d * Math.PI * 7000d * time);
            double mixed = Math.Clamp((carrier + overtone + hiss) * 0.35d, -1d, 1d);
            samples[i] = (short)Math.Round(mixed * short.MaxValue);
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
