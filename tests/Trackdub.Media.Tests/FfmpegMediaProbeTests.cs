using Trackdub.Media.Probe;

namespace Trackdub.Media.Tests;

public sealed class FfmpegMediaProbeTests
{
    [RequiresFfmpegAndFfprobeFact]
    public async Task ProbeAsync_reads_fixture_media_metadata()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        string sourcePath = await MediaFixtureFactory.CreateSampleVideoAsync(tempDirectory);
        var probe = new FfmpegMediaProbe();

        var result = await probe.ProbeAsync(sourcePath, TestContext.Current.CancellationToken);

        Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", result.FormatName);
        Assert.True(result.DurationSeconds > 1d);
        Assert.NotEmpty(result.AudioStreams);
        Assert.NotEmpty(result.VideoStreams);
        Assert.True(result.AudioStreams[0].SampleRate > 0);
        Assert.True(result.VideoStreams[0].Width > 0);
    }
}
