using System.Runtime.InteropServices;
using Trackdub.Domain.Media;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;

namespace Trackdub.Media.Playback;

/// <summary>
/// LibVLC-backed playback backend that copies decoded video frames into an app-owned frame sink
/// so Avalonia can compose overlays inside its own visual tree.
/// </summary>
public sealed class LibVlcCompositedPlaybackBackend :
    IPlaybackBackend,
    IPlaybackRateBackend,
    IPlaybackVolumeBackend,
    IPlaybackFrameSinkAwareBackend,
    IPlaybackPreviewFrameBackend,
    IDisposable
{
    private const string PixelFormat = "RV32";

    private readonly string runtimePath;
    private readonly MediaPlayer.LibVLCVideoLockCb lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb displayCallback;
    private readonly object frameGate = new();

    private LibVLC? libVlc;
    private MediaPlayer? mediaPlayer;
    private VlcMedia? currentMedia;
    private IPlaybackFrameSink? frameSink;
    private string? warningMessage;
    private IntPtr frameBuffer = IntPtr.Zero;
    private int frameWidth;
    private int frameHeight;
    private int frameStride;
    private bool isLoaded;

    public LibVlcCompositedPlaybackBackend(string runtimePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        this.runtimePath = runtimePath;
        lockCallback = LockVideo;
        unlockCallback = UnlockVideo;
        displayCallback = DisplayVideo;
    }

    public bool TryAttachFrameSink(IPlaybackFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        frameSink = sink;
        if (frameWidth > 0 && frameHeight > 0 && frameStride > 0)
        {
            sink.OnVideoFormatChanged(new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat));
        }

        return true;
    }

    public async Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);

        ReleaseMediaResources();
        warningMessage = null;

        if (!File.Exists(source.SourcePath))
        {
            warningMessage = $"Source file not found: {source.SourcePath}";
            isLoaded = false;
            return;
        }

        if (source.Probe.VideoStreams.Count == 0)
        {
            warningMessage = "Composited playback requires a probed video stream.";
            isLoaded = false;
            return;
        }

        MediaVideoStream? primaryVideo = source.Probe.VideoStreams.FirstOrDefault();
        frameWidth = primaryVideo?.Width ?? 0;
        frameHeight = primaryVideo?.Height ?? 0;

        try
        {
            Core.Initialize(runtimePath);
            libVlc = new LibVLC();
            mediaPlayer = new MediaPlayer(libVlc);
            currentMedia = new VlcMedia(libVlc, source.SourcePath, FromType.FromPath);
            MediaParsedStatus parseResult = await currentMedia.Parse(MediaParseOptions.ParseLocal).ConfigureAwait(false);

            if (parseResult != MediaParsedStatus.Done)
            {
                warningMessage = "VLC failed to parse the media file.";
                isLoaded = false;
                ReleaseMediaResources();
                return;
            }

            TryRefreshFrameDimensionsFromParsedMedia();
            if (frameWidth <= 0 || frameHeight <= 0)
            {
                warningMessage = "Composited playback requires a video stream with known dimensions.";
                isLoaded = false;
                ReleaseMediaResources();
                return;
            }

            frameStride = checked(frameWidth * 4);
            frameBuffer = Marshal.AllocHGlobal(checked(frameStride * frameHeight));
            mediaPlayer.SetVideoFormat(PixelFormat, (uint)frameWidth, (uint)frameHeight, (uint)frameStride);
            mediaPlayer.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);

            mediaPlayer.Media = currentMedia;
            frameSink?.OnVideoFormatChanged(new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat));
            isLoaded = true;
            warningMessage = null;
        }
        catch (Exception ex)
        {
            warningMessage = $"LibVLC compositor initialization failed: {ex.Message}";
            isLoaded = false;
            ReleaseMediaResources();
        }
    }

    public async Task PreparePreviewFrameAsync(CancellationToken ct)
    {
        if (!isLoaded || mediaPlayer is null)
        {
            return;
        }

        await Task.Run(
            async () =>
            {
                mediaPlayer.Play();
                await Task.Delay(150, ct).ConfigureAwait(false);
                mediaPlayer.Pause();
                mediaPlayer.Time = 0;
            },
            ct).ConfigureAwait(false);
    }

    public Task PlayAsync(CancellationToken ct)
    {
        mediaPlayer?.Play();
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        mediaPlayer?.Pause();
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        if (mediaPlayer is not null && isLoaded)
        {
            mediaPlayer.Time = (long)position.TotalMilliseconds;
        }

        return Task.CompletedTask;
    }

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (!isLoaded || mediaPlayer is null)
        {
            return Task.FromResult(PlaybackSnapshot.Empty with { WarningMessage = warningMessage });
        }

        return Task.FromResult(new PlaybackSnapshot(
            IsLoaded: true,
            IsPlaying: mediaPlayer.IsPlaying,
            Position: TimeSpan.FromMilliseconds(mediaPlayer.Time),
            Duration: TimeSpan.FromMilliseconds(mediaPlayer.Length),
            PlaybackRate: mediaPlayer.Rate,
            WarningMessage: warningMessage));
    }

    public Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.SetRate((float)playbackRate);
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.Volume = (int)(Math.Clamp(volume, 0d, 1d) * 100d);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => ReleaseMediaResources();

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        lock (frameGate)
        {
            if (frameBuffer != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(planes, frameBuffer);
            }
        }

        return IntPtr.Zero;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void TryRefreshFrameDimensionsFromParsedMedia()
    {
        if (currentMedia is null)
        {
            return;
        }

        foreach (MediaTrack track in currentMedia.Tracks)
        {
            if (track.TrackType != TrackType.Video)
            {
                continue;
            }

            uint width = track.Data.Video.Width;
            uint height = track.Data.Video.Height;
            if (width > 0 && height > 0)
            {
                frameWidth = (int)width;
                frameHeight = (int)height;
                return;
            }
        }
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (frameSink is null || frameBuffer == IntPtr.Zero || frameStride <= 0 || frameHeight <= 0)
        {
            return;
        }

        byte[] managedFrame = new byte[checked(frameStride * frameHeight)];
        lock (frameGate)
        {
            Marshal.Copy(frameBuffer, managedFrame, 0, managedFrame.Length);
        }

        frameSink.OnVideoFrameArrived(new VideoFrame(
            new VideoFrameDescriptor(frameWidth, frameHeight, frameStride, PixelFormat),
            managedFrame));
    }

    private void ReleaseMediaResources()
    {
        try
        {
            mediaPlayer?.Stop();
        }
        catch
        {
            // Ignore teardown errors from native player.
        }

        mediaPlayer?.Dispose();
        mediaPlayer = null;
        currentMedia?.Dispose();
        currentMedia = null;
        libVlc?.Dispose();
        libVlc = null;

        lock (frameGate)
        {
            if (frameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(frameBuffer);
                frameBuffer = IntPtr.Zero;
            }
        }

        frameWidth = 0;
        frameHeight = 0;
        frameStride = 0;
        isLoaded = false;
        frameSink?.OnVideoSurfaceCleared();
    }
}
