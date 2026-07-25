using Trackdub.TestDoubles;
using Trackdub.Domain.Media;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class PlaybackCapabilityProbeTests
{
    [Fact]
    public void Assess_prefers_media_foundation_for_common_mp4()
    {
        var probe = new PlaybackCapabilityProbe();
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mov,mp4,m4a,3gp,3g2,mj2",
                "QuickTime / MOV",
                12.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 12.0)],
                [new MediaVideoStream(1, "h264", 1920, 1080, 24.0, 12.0)]));

        PlaybackCapabilityAssessment assessment = probe.Assess(source);

        Assert.Equal(PlaybackBackendKind.MediaFoundation, assessment.PreferredBackend);
        Assert.True(assessment.IsLikelySupportedByCurrentWindowsMediaStack);
        Assert.Equal("h264", assessment.VideoCodec);
        Assert.Equal("aac", assessment.AudioCodec);
    }

    [Fact]
    public void Assess_routes_uncommon_container_to_fallback_and_flags_hdr()
    {
        var probe = new PlaybackCapabilityProbe();
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mkv"),
            new MediaProbeSnapshot(
                "matroska,webm",
                "Matroska / WebM",
                12.0,
                4096,
                [new MediaAudioStream(0, "opus", 2, 48000, 12.0)],
                [new MediaVideoStream(1, "prores", 1920, 1080, 24.0, 12.0, ColorTransfer: "smpte2084")],
                [new MediaSubtitleStream(2, "ass", "eng")]));

        PlaybackCapabilityAssessment assessment = probe.Assess(source);

        Assert.Equal(PlaybackBackendKind.FfmpegFallback, assessment.PreferredBackend);
        Assert.False(assessment.IsLikelySupportedByCurrentWindowsMediaStack);
        Assert.True(assessment.IsHdrLikely);
        Assert.Equal(1, assessment.SubtitleTrackCount);
        Assert.Contains("Windows native playback is unlikely", assessment.WarningMessage);
    }
}

public sealed class PlaybackServiceTests
{
    [Fact]
    public async Task Open_seek_and_rate_change_flow_through_selected_backend()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);
        await service.SeekAsync(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
        await service.SetPlaybackRateAsync(1.25, TestContext.Current.CancellationToken);
        await service.PlayAsync(TestContext.Current.CancellationToken);
        PlaybackSnapshot snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.Equal(TimeSpan.FromSeconds(2.5), snapshot.Position);
        Assert.Equal(1.25, snapshot.PlaybackRate);
        Assert.True(snapshot.IsPlaying);
    }

    [Fact]
    public async Task SetVolumeAsync_clamps_and_flows_through_selected_backend()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        await service.OpenAsync(source, TestContext.Current.CancellationToken);
        await service.SetVolumeAsync(0.25d, TestContext.Current.CancellationToken);
        Assert.Equal(0.25d, backend.Volume, 3);

        await service.SetVolumeAsync(2d, TestContext.Current.CancellationToken);
        Assert.Equal(1d, backend.Volume, 3);

        await service.SetVolumeAsync(double.NaN, TestContext.Current.CancellationToken);
        Assert.Equal(1d, backend.Volume, 3);
    }

    [Fact]
    public async Task Open_leaves_playback_unavailable_when_required_fallback_backend_is_missing()
    {
        var service = new PlaybackService(new PlaybackCapabilityProbe(), new FakePlaybackBackendFactory());
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mkv"),
            new MediaProbeSnapshot(
                "matroska,webm",
                "Matroska / WebM",
                12.0,
                4096,
                [new MediaAudioStream(0, "opus", 2, 48000, 12.0)],
                [new MediaVideoStream(1, "prores", 1920, 1080, 24.0, 12.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.False(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.FfmpegFallback, openResult.Assessment.PreferredBackend);
        Assert.Contains("not implemented", openResult.Snapshot.WarningMessage);
    }

    [Fact]
    public async Task Open_surfaces_runtime_media_failure_warning_for_supported_source()
    {
        var backend = new FakePlaybackBackend
        {
            WarningOnOpen = "Media Foundation failed to open or play this source (Network)."
        };
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        Assert.Contains("failed to open or play", openResult.Snapshot.WarningMessage);
    }

    [Fact]
    public async Task Open_with_LibMpv_backend_kind_flows_through_fake_factory()
    {
        var backend = new FakePlaybackBackend();
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var service = new PlaybackService(
            probe,
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.LibMpv, backend));
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.True(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibMpv, openResult.Assessment.PreferredBackend);
    }

    [Fact]
    public async Task Open_falls_back_from_libmpv_to_libvlc_when_libmpv_open_fails()
    {
        var mpvBackend = new FakePlaybackBackend { WarningOnOpen = "libmpv compositor initialization failed." };
        var vlcBackend = new FakePlaybackBackend();
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var factory = new FakePlaybackBackendFactory()
            .Add(PlaybackBackendKind.LibMpv, mpvBackend)
            .Add(PlaybackBackendKind.LibVlc, vlcBackend);
        var service = new PlaybackService(probe, factory);
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.True(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibVlc, openResult.Assessment.PreferredBackend);
        Assert.NotNull(openResult.Assessment.WarningMessage);
        Assert.Contains("LibVLC fallback", openResult.Assessment.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_reports_unavailable_when_libmpv_and_libvlc_both_fail_to_load()
    {
        var mpvBackend = new FakePlaybackBackend { WarningOnOpen = "libmpv failed." };
        var vlcBackend = new FakePlaybackBackend { WarningOnOpen = "VLC failed." };
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var factory = new FakePlaybackBackendFactory()
            .Add(PlaybackBackendKind.LibMpv, mpvBackend)
            .Add(PlaybackBackendKind.LibVlc, vlcBackend);
        var service = new PlaybackService(probe, factory);
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.False(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibVlc, openResult.Assessment.PreferredBackend);
    }

    [Fact]
    public async Task PreparePreviewFrameAsync_flows_through_compositor_backend()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));
        var sink = new RecordingFrameSink();

        await service.OpenAsync(source, TestContext.Current.CancellationToken);
        await service.TryAttachFrameSinkAsync(sink, TestContext.Current.CancellationToken);
        await service.PreparePreviewFrameAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sink.LastFrame);
    }

    [Fact]
    public async Task TryAttachFrameSink_flows_through_selected_backend()
    {
        var backend = new FakePlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));
        var sink = new RecordingFrameSink();

        await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.True(await service.TryAttachFrameSinkAsync(sink, TestContext.Current.CancellationToken));
        Assert.Same(sink, backend.AttachedFrameSink);
    }

    [Fact]
    public async Task Open_reports_unavailable_when_LibMpv_backend_is_missing_from_factory()
    {
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var service = new PlaybackService(probe, new FakePlaybackBackendFactory());
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.False(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibMpv, openResult.Assessment.PreferredBackend);
        Assert.Contains("libmpv runtime", openResult.Snapshot.WarningMessage);
    }

    // ── Regression tests: backend throws instead of returning a warning ──────────
    // Prior to the fix, LibMpvCompositedPlaybackBackend.OpenAsync() began propagating
    // exceptions (OperationCanceledException and InvalidOperationException) instead of
    // silently setting a WarningMessage. PlaybackService.OpenAsync() had no catch clause
    // around primary.OpenAsync(), so the exception escaped to ApplyProjectStateAsync,
    // which showed a generic "UI refresh failed" message instead of a playback warning.
    // The fix wraps backend.OpenAsync() calls in PlaybackService with exception handlers
    // that convert thrown exceptions to IsBackendAvailable=false + WarningMessage.

    [Fact]
    public async Task Open_returns_unavailable_when_libmpv_backend_throws_instead_of_returning_warning()
    {
        var mpvBackend = new FakePlaybackBackend
        {
            ExceptionOnOpen = new InvalidOperationException("libmpv compositor initialization failed.")
        };
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var factory = new FakePlaybackBackendFactory()
            .Add(PlaybackBackendKind.LibMpv, mpvBackend);
        var service = new PlaybackService(probe, factory);
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.False(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        // The exception message must surface as a warning so the UI can display it,
        // rather than escaping as an unhandled exception and showing a generic error.
        Assert.Contains("libmpv compositor initialization failed", openResult.Snapshot.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_falls_back_to_libvlc_when_libmpv_throws_and_libvlc_succeeds()
    {
        var mpvBackend = new FakePlaybackBackend
        {
            ExceptionOnOpen = new InvalidOperationException("libmpv hw decode unavailable.")
        };
        var vlcBackend = new FakePlaybackBackend();
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var factory = new FakePlaybackBackendFactory()
            .Add(PlaybackBackendKind.LibMpv, mpvBackend)
            .Add(PlaybackBackendKind.LibVlc, vlcBackend);
        var service = new PlaybackService(probe, factory);
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.True(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibVlc, openResult.Assessment.PreferredBackend);
        Assert.Contains("LibVLC fallback", openResult.Assessment.WarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_returns_unavailable_when_libmpv_throws_and_libvlc_also_throws()
    {
        var mpvBackend = new FakePlaybackBackend
        {
            ExceptionOnOpen = new InvalidOperationException("libmpv hw decode failed.")
        };
        var vlcBackend = new FakePlaybackBackend
        {
            ExceptionOnOpen = new InvalidOperationException("LibVLC init failed.")
        };
        var probe = new LibMpvPlaybackCapabilityProbeStub();
        var factory = new FakePlaybackBackendFactory()
            .Add(PlaybackBackendKind.LibMpv, mpvBackend)
            .Add(PlaybackBackendKind.LibVlc, vlcBackend);
        var service = new PlaybackService(probe, factory);
        var source = new MediaSourceDescriptor(
            @"D:\media\sample.mp4",
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        PlaybackOpenResult openResult = await service.OpenAsync(source, TestContext.Current.CancellationToken);

        Assert.False(openResult.IsBackendAvailable);
        Assert.False(openResult.Snapshot.IsLoaded);
        Assert.Equal(PlaybackBackendKind.LibVlc, openResult.Assessment.PreferredBackend);
        Assert.NotNull(openResult.Snapshot.WarningMessage);
    }

    [Fact]
    public async Task Transport_commands_no_op_without_blocking_while_open_is_in_progress()
    {
        // OpenAsync's slow phase (primary.OpenAsync) runs gate-free specifically so callers
        // like the position timer and transport commands never queue up behind it. A
        // transport command issued mid-open must return promptly and skip the backend
        // entirely (State == Opening) rather than block until the open completes.
        var backend = new BlockingOpenPlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        Task<PlaybackOpenResult> openTask = service.OpenAsync(source, TestContext.Current.CancellationToken);
        await backend.OpenStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackendState.Opening, service.State);

        Task playTask = service.PlayAsync(TestContext.Current.CancellationToken);
        Task firstCompleted = await Task.WhenAny(
            playTask,
            Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));

        Assert.Same(playTask, firstCompleted);
        Assert.False(backend.PlayWasCalled);

        backend.OpenCanComplete.SetResult();
        PlaybackOpenResult openResult = await openTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(openResult.IsBackendAvailable);
        Assert.Equal(BackendState.Ready, service.State);
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_empty_immediately_while_open_is_in_progress()
    {
        var backend = new BlockingOpenPlaybackBackend();
        var service = new PlaybackService(
            new PlaybackCapabilityProbe(),
            new FakePlaybackBackendFactory().Add(PlaybackBackendKind.MediaFoundation, backend));
        var source = new MediaSourceDescriptor(
            Path.Combine("virtual-media", "sample.mp4"),
            new MediaProbeSnapshot(
                "mp4",
                "MP4",
                5.0,
                2048,
                [new MediaAudioStream(0, "aac", 2, 44100, 5.0)],
                [new MediaVideoStream(1, "h264", 1280, 720, 24.0, 5.0)]));

        Task<PlaybackOpenResult> openTask = service.OpenAsync(source, TestContext.Current.CancellationToken);
        await backend.OpenStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        PlaybackSnapshot snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackSnapshot.Empty, snapshot);

        backend.OpenCanComplete.SetResult();
        await openTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void WaveformMapping_converts_time_and_pixels_consistently()
    {
        float x = WaveformMapping.TimeToPixel(5.0, 10.0, 200f);
        double seconds = WaveformMapping.PixelToTime(x, 10.0, 200f);

        Assert.Equal(100f, x, 3);
        Assert.Equal(5.0, seconds, 3);
    }
}

/// <summary>
/// A stub probe that always returns libmpv as the preferred backend,
/// simulating the Avalonia shell's AvaloniaPlaybackCapabilityProbe behavior.
/// </summary>
internal sealed class LibMpvPlaybackCapabilityProbeStub : PlaybackCapabilityProbe
{
    public override PlaybackCapabilityAssessment Assess(MediaSourceDescriptor source)
    {
        PlaybackCapabilityAssessment baseAssessment = base.Assess(source);
        return baseAssessment with { PreferredBackend = PlaybackBackendKind.LibMpv };
    }
}

internal sealed class RecordingFrameSink : IPlaybackFrameSink
{
    public VideoFrameDescriptor? LastFormat { get; private set; }

    public VideoFrame? LastFrame { get; private set; }

    public bool WasCleared { get; private set; }

    public void OnVideoFormatChanged(VideoFrameDescriptor format) => LastFormat = format;

    public void OnVideoFrameArrived(VideoFrame frame) => LastFrame = frame;

    public void OnVideoSurfaceCleared() => WasCleared = true;
}

internal sealed class BlockingOpenPlaybackBackend : IPlaybackBackend
{
    private bool openCompleted;

    public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource OpenCanComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool PlayWasCalled { get; private set; }

    public bool PlayCalledBeforeOpenCompleted { get; private set; }

    public async Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        OpenStarted.SetResult();
        await OpenCanComplete.Task.WaitAsync(ct).ConfigureAwait(false);
        openCompleted = true;
    }

    public Task PlayAsync(CancellationToken ct)
    {
        PlayWasCalled = true;
        PlayCalledBeforeOpenCompleted = !openCompleted;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;

    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Task.CompletedTask;

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(PlaybackSnapshot.Empty with
        {
            IsLoaded = openCompleted,
            Duration = TimeSpan.FromSeconds(5)
        });
}
