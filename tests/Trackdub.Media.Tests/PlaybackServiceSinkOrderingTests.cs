using Trackdub.Domain.Media;
using Trackdub.Media.Playback;
using Trackdub.TestDoubles;

namespace Trackdub.Media.Tests;

/// <summary>
/// Guards the frame-sink attachment ordering contract in <see cref="PlaybackService"/>:
/// a sink registered before <c>OpenAsync</c> must receive frames once the backend is ready,
/// and a sink attached after open must receive frames immediately.
///
/// Root cause history (2026-05-31): <c>TryAttachFrameSink</c> was called before
/// <c>OpenAsync</c> in <c>OpenPlaybackForStateAsync</c>, but the original implementation
/// only forwarded the sink to the <em>current</em> backend — which did not exist yet when
/// the call was made pre-open.  The fix stores the sink as <c>pendingFrameSink</c> and
/// re-applies it every time a backend is selected, so the ordering no longer matters.
/// </summary>
public sealed class PlaybackServiceSinkOrderingTests
{
    private static MediaSourceDescriptor MakeSource() =>
        new(
            System.IO.Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

    [Fact]
    public async Task Sink_attached_before_open_receives_frames_after_open()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var sink = new RecordingFrameSink();

        // Register sink BEFORE open — this is the ordering used by OpenPlaybackForStateAsync.
        await service.TryAttachFrameSinkAsync(sink, CancellationToken.None);

        await service.OpenAsync(MakeSource(), CancellationToken.None);
        await service.PreparePreviewFrameAsync(CancellationToken.None);

        Assert.NotNull(sink.LastFrame);
        Assert.Same(sink, backend.AttachedFrameSink);
    }

    [Fact]
    public async Task Sink_attached_after_open_receives_frames_on_next_prepare()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var sink = new RecordingFrameSink();

        await service.OpenAsync(MakeSource(), CancellationToken.None);

        // Register sink AFTER open.
        await service.TryAttachFrameSinkAsync(sink, CancellationToken.None);
        await service.PreparePreviewFrameAsync(CancellationToken.None);

        Assert.NotNull(sink.LastFrame);
        Assert.Same(sink, backend.AttachedFrameSink);
    }

    [Fact]
    public async Task Sink_attached_before_second_open_receives_frames_from_new_backend()
    {
        // A second call to OpenAsync replaces the internal backend.  A sink registered
        // before the second open must be forwarded to the new backend automatically.
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));

        // First open with an earlier sink.
        var firstSink = new RecordingFrameSink();
        await service.TryAttachFrameSinkAsync(firstSink, CancellationToken.None);
        await service.OpenAsync(MakeSource(), CancellationToken.None);

        // Replace sink before second open (matches how OpenPlaybackForStateAsync calls TryAttachFrameSink).
        var secondSink = new RecordingFrameSink();
        await service.TryAttachFrameSinkAsync(secondSink, CancellationToken.None);
        await service.OpenAsync(MakeSource(), CancellationToken.None);
        await service.PreparePreviewFrameAsync(CancellationToken.None);

        Assert.NotNull(secondSink.LastFrame);
        Assert.Same(secondSink, backend.AttachedFrameSink);
    }

    [Fact]
    public async Task Reset_clears_pending_sink_so_next_open_does_not_forward_stale_sink()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var staleSink = new RecordingFrameSink();

        await service.TryAttachFrameSinkAsync(staleSink, CancellationToken.None);
        await service.ResetAsync(CancellationToken.None);

        await service.OpenAsync(MakeSource(), CancellationToken.None);
        await service.PreparePreviewFrameAsync(CancellationToken.None);

        // After Reset, no sink was re-registered — the stale one must not receive frames.
        Assert.Null(staleSink.LastFrame);
        Assert.Null(backend.AttachedFrameSink);
    }
}
