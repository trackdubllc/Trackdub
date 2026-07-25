using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class Pcm16ReferenceClipTrimmerTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "Trackdub.ReferenceClipTrimmer",
        Guid.NewGuid().ToString("N"));

    public Pcm16ReferenceClipTrimmerTests()
    {
        Directory.CreateDirectory(tempDirectory);
    }

    [Fact]
    public async Task TrimAsync_removes_edge_silence_and_insets_into_active_audio()
    {
        string path = Path.Combine(tempDirectory, "reference.wav");
        const int sampleRate = 1000;
        float[] samples =
        [
            ..Enumerable.Repeat(0f, 100),
            ..Enumerable.Repeat(0.25f, 1000),
            ..Enumerable.Repeat(0f, 100)
        ];
        await WavePcm16.WriteMonoAsync(path, samples, sampleRate, TestContext.Current.CancellationToken);

        var trimmer = new Pcm16ReferenceClipTrimmer();

        var result = await trimmer.TrimAsync(path, TestContext.Current.CancellationToken);

        WaveMonoSamples trimmed = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.True(result.Trimmed);
        Assert.Equal(0.15d, result.TrimmedLeadingSeconds, precision: 3);
        Assert.Equal(0.15d, result.TrimmedTrailingSeconds, precision: 3);
        Assert.Equal(900, trimmed.Samples.Length);
        Assert.All(trimmed.Samples, sample => Assert.True(Math.Abs(sample) >= 0.015f));
    }

    [Fact]
    public async Task TrimAsync_does_not_trim_fully_active_clip_without_edge_silence()
    {
        string path = Path.Combine(tempDirectory, "reference_active.wav");
        const int sampleRate = 1000;
        float[] samples =
        [
            ..Enumerable.Repeat(0.25f, 1000)
        ];
        await WavePcm16.WriteMonoAsync(path, samples, sampleRate, TestContext.Current.CancellationToken);

        var trimmer = new Pcm16ReferenceClipTrimmer();

        var result = await trimmer.TrimAsync(path, TestContext.Current.CancellationToken);

        WaveMonoSamples trimmed = await WavePcm16.ReadAllMonoSamplesAsync(path, TestContext.Current.CancellationToken);
        Assert.False(result.Trimmed);
        Assert.Equal(1000, trimmed.Samples.Length);
        Assert.Equal(1.0d, result.OriginalDurationSeconds);
        Assert.Equal(1.0d, result.TrimmedDurationSeconds);
        Assert.Equal(0d, result.TrimmedLeadingSeconds);
        Assert.Equal(0d, result.TrimmedTrailingSeconds);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
