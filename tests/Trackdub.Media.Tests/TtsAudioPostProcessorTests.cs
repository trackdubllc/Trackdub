using System.Security.AccessControl;
using System.Security.Principal;
using Trackdub.Contracts;
using Trackdub.Media.Tts;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class TtsAudioPostProcessorTests : IDisposable
{
    private const float SampleTolerance = 0.0001f;
    private readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));

    public TtsAudioPostProcessorTests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task ProcessAsync_leaves_all_silence_unchanged()
    {
        const int sampleRate = 16000;
        float[] samples = new float[32];
        string path = await WriteWaveAsync("all-silence.wav", samples, sampleRate);

        TtsAudioPostProcessResult result = await CreateProcessor().ProcessAsync(
            new TtsAudioPostProcessRequest(path, sampleRate, samples.Length),
            TestContext.Current.CancellationToken);

        WaveMonoSamples output = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(sampleRate, result.SampleRate);
        Assert.Equal(samples.Length, result.DurationSamples);
        Assert.Equal(0, result.LeadingTrimmedSamples);
        Assert.Equal(0, result.TrailingTrimmedSamples);
        Assert.Equal(samples.Length, output.Samples.Length);
        Assert.All(output.Samples, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task ProcessAsync_returns_unchanged_result_when_input_wave_cannot_be_read()
    {
        const int sampleRate = 16000;
        const int durationSamples = 123;
        string path = Path.Combine(tempDirectory, "invalid.wav");
        await File.WriteAllTextAsync(path, "not-a-wave", TestContext.Current.CancellationToken);
        var logger = new RecordingApplicationLogger();

        TtsAudioPostProcessResult result = await CreateProcessor(logger).ProcessAsync(
            new TtsAudioPostProcessRequest(path, sampleRate, durationSamples),
            TestContext.Current.CancellationToken);

        Assert.Equal(sampleRate, result.SampleRate);
        Assert.Equal(durationSamples, result.DurationSamples);
        Assert.Equal(0, result.LeadingTrimmedSamples);
        Assert.Equal(0, result.TrailingTrimmedSamples);
        Assert.Contains(logger.Warnings, warning => warning.Contains(path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessAsync_ignores_trims_smaller_than_minimum_trim_window()
    {
        const int sampleRate = 1000;
        float[] samples =
        [
            0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0.75f, 0.75f, 0.75f, 0.75f, 0.75f, 0.75f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f
        ];
        string path = await WriteWaveAsync("below-minimum-trim.wav", samples, sampleRate);
        WaveMonoSamples before = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);

        TtsAudioPostProcessResult result = await CreateProcessor().ProcessAsync(
            new TtsAudioPostProcessRequest(path, sampleRate, samples.Length),
            TestContext.Current.CancellationToken);

        WaveMonoSamples after = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(sampleRate, result.SampleRate);
        Assert.Equal(samples.Length, result.DurationSamples);
        Assert.Equal(0, result.LeadingTrimmedSamples);
        Assert.Equal(0, result.TrailingTrimmedSamples);
        AssertSamplesEqual(before.Samples, after.Samples);
    }

    [Fact]
    public async Task ProcessAsync_preserves_padding_around_audible_content_when_trimming()
    {
        const int sampleRate = 1000;
        float[] samples =
        [
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f, 0f
        ];
        string path = await WriteWaveAsync("preserve-padding.wav", samples, sampleRate);

        TtsAudioPostProcessResult result = await CreateProcessor().ProcessAsync(
            new TtsAudioPostProcessRequest(path, sampleRate, samples.Length),
            TestContext.Current.CancellationToken);

        WaveMonoSamples output = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(sampleRate, result.SampleRate);
        Assert.Equal(20, result.DurationSamples);
        Assert.Equal(15, result.LeadingTrimmedSamples);
        Assert.Equal(10, result.TrailingTrimmedSamples);
        Assert.Equal(20, output.Samples.Length);
        Assert.All(output.Samples.Take(5), sample => Assert.Equal(0f, sample));
        Assert.All(output.Samples.Skip(5).Take(10), sample => Assert.InRange(sample, 0.59f, 0.61f));
        Assert.All(output.Samples.Skip(15), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task ProcessAsync_handles_very_short_clips_with_unusual_sample_rates()
    {
        const int sampleRate = 199;
        float[] samples = [0f, 0f, 0.8f, 0f, 0f];
        string path = await WriteWaveAsync("short-unusual-rate.wav", samples, sampleRate);

        TtsAudioPostProcessResult result = await CreateProcessor().ProcessAsync(
            new TtsAudioPostProcessRequest(path, sampleRate, samples.Length),
            TestContext.Current.CancellationToken);

        WaveMonoSamples output = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(sampleRate, result.SampleRate);
        Assert.Equal(3, result.DurationSamples);
        Assert.Equal(1, result.LeadingTrimmedSamples);
        Assert.Equal(1, result.TrailingTrimmedSamples);
        Assert.Equal(3, output.Samples.Length);
        Assert.Equal(0f, output.Samples[0]);
        Assert.InRange(output.Samples[1], 0.79f, 0.81f);
        Assert.Equal(0f, output.Samples[2]);
    }

    [Fact]
    public async Task ProcessAsync_propagates_write_failures_after_trim_read_succeeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int sampleRate = 1000;
        float[] samples =
        [
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f, 0.7f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f, 0f
        ];
        string path = await WriteWaveAsync("locked-output.wav", samples, sampleRate);

        // WriteSamplesAsync publishes via a sibling temp file then File.Move. Denying Write/Delete on
        // the destination alone no longer blocks the write (the temp create still succeeds). Deny
        // CreateFiles on the parent directory so the temp path cannot be created, while the existing
        // destination remains readable for the trim pass and the final integrity check.
        var directoryInfo = new DirectoryInfo(tempDirectory);
        DirectorySecurity originalDirectorySecurity = directoryInfo.GetAccessControl();
        DirectorySecurity deniedCreateSecurity = directoryInfo.GetAccessControl();
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);
        deniedCreateSecurity.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.CreateFiles,
            AccessControlType.Deny));

        try
        {
            directoryInfo.SetAccessControl(deniedCreateSecurity);

            Exception? exception = await Record.ExceptionAsync(() => CreateProcessor().ProcessAsync(
                new TtsAudioPostProcessRequest(path, sampleRate, samples.Length),
                TestContext.Current.CancellationToken));

            Assert.NotNull(exception);
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected an I/O or access error, but got {exception.GetType().FullName}: {exception.Message}");
        }
        finally
        {
            directoryInfo.SetAccessControl(originalDirectorySecurity);
        }

        WaveMonoSamples output = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);
        AssertSamplesEqual(samples, output.Samples);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static TtsAudioPostProcessor CreateProcessor(RecordingApplicationLogger? logger = null) => new(logger);

    private static void AssertSamplesEqual(IReadOnlyList<float> expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.True(
                Math.Abs(expected[index] - actual[index]) <= SampleTolerance,
                $"Sample mismatch at index {index}: expected {expected[index]}, actual {actual[index]}.");
        }
    }

    private async Task<string> WriteWaveAsync(string fileName, float[] samples, int sampleRate)
    {
        string path = Path.Combine(tempDirectory, fileName);
        await WavePcm16.WriteMonoAsync(path, samples, sampleRate, TestContext.Current.CancellationToken);
        return path;
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        private readonly List<string> warnings = [];

        public IReadOnlyList<string> Warnings => warnings;

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message, Exception? exception = null)
        {
            warnings.Add(exception is null ? message : $"{message}: {exception.Message}");
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }
}
