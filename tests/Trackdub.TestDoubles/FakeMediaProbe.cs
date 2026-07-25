using Trackdub.Contracts;
using Trackdub.Domain.Media;

namespace Trackdub.TestDoubles;

public sealed class FakeMediaProbe : IMediaProbe
{
    private readonly List<string> calls = [];

    public IReadOnlyList<string> Calls => calls;

    public MediaProbeSnapshot Snapshot { get; set; } = new(
        "mp4",
        "MP4",
        DurationSeconds: 1d,
        BitRate: null,
        AudioStreams:
        [
            new MediaAudioStream(0, "aac", Channels: 2, SampleRate: 48000, DurationSeconds: 1d)
        ],
        VideoStreams:
        [
            new MediaVideoStream(1, "h264", Width: 64, Height: 64, FrameRate: 24d, DurationSeconds: 1d)
        ],
        SubtitleStreams: []);

    public Task<MediaProbeSnapshot> ProbeAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(sourcePath);
        return Task.FromResult(Snapshot);
    }
}
