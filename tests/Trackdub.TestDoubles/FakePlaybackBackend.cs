using Trackdub.Media.Playback;

namespace Trackdub.TestDoubles;

public sealed class FakePlaybackBackend :
    IPlaybackBackend,
    IPlaybackRateBackend,
    IPlaybackVolumeBackend,
    IPlaybackFrameSinkAwareBackend,
    IPlaybackPreviewFrameBackend
{
    private PlaybackSnapshot snapshot = PlaybackSnapshot.Empty with
    {
        IsLoaded = true,
        Duration = TimeSpan.FromSeconds(5),
        PlaybackRate = 1d
    };

    public string? WarningOnOpen { get; set; }

    /// <summary>
    /// When set, <see cref="OpenAsync"/> throws this exception instead of returning normally.
    /// Simulates the behavior introduced in commit c88fd5f3 where LibMpvCompositedPlaybackBackend
    /// propagates exceptions instead of silently setting a warning message.
    /// </summary>
    public Exception? ExceptionOnOpen { get; set; }

    public double Volume { get; private set; } = 1d;

    public IPlaybackFrameSink? AttachedFrameSink { get; private set; }

    public Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        if (ExceptionOnOpen is not null)
        {
            return Task.FromException(ExceptionOnOpen);
        }

        snapshot = snapshot with
        {
            IsLoaded = string.IsNullOrWhiteSpace(WarningOnOpen),
            IsPlaying = false,
            Position = TimeSpan.Zero,
            WarningMessage = WarningOnOpen
        };

        return Task.CompletedTask;
    }

    public Task PlayAsync(CancellationToken ct)
    {
        snapshot = snapshot with { IsPlaying = true };
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        snapshot = snapshot with { IsPlaying = false };
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        snapshot = snapshot with { Position = position };
        return Task.CompletedTask;
    }

    public Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct)
    {
        snapshot = snapshot with { PlaybackRate = playbackRate };
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0d, 1d) : 1d;
        return Task.CompletedTask;
    }

    public bool TryAttachFrameSink(IPlaybackFrameSink sink)
    {
        AttachedFrameSink = sink;
        return true;
    }

    public Task PreparePreviewFrameAsync(CancellationToken ct)
    {
        if (AttachedFrameSink is null)
        {
            return Task.CompletedTask;
        }

        var format = new VideoFrameDescriptor(320, 180, 320 * 4, "bgr0");
        byte[] pixels = new byte[format.Stride * format.Height];
        for (int index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        AttachedFrameSink.OnVideoFormatChanged(format);
        AttachedFrameSink.OnVideoFrameArrived(new VideoFrame(format, pixels));
        return Task.CompletedTask;
    }

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(snapshot);
}

public sealed class FakePlaybackBackendFactory : IPlaybackBackendFactory
{
    private readonly Dictionary<PlaybackBackendKind, IPlaybackBackend> backends = new();

    public FakePlaybackBackendFactory Add(PlaybackBackendKind backendKind, IPlaybackBackend backend)
    {
        backends[backendKind] = backend;
        return this;
    }

    public IPlaybackBackend? Create(PlaybackBackendKind backendKind) =>
        backends.TryGetValue(backendKind, out IPlaybackBackend? backend)
            ? backend
            : null;
}
