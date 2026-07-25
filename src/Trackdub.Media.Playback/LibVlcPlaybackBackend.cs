using LibVLCSharp.Shared;

using VlcMedia = LibVLCSharp.Shared.Media;

namespace Trackdub.Media.Playback;

/// <summary>
/// LibVLC-backed playback backend for cross-platform video playback.
/// Implements play/pause/seek/rate/volume with honest error reporting.
/// </summary>
public sealed class LibVlcPlaybackBackend :
    IPlaybackBackend,
    IPlaybackHostAwareBackend,
    IPlaybackRateBackend,
    IPlaybackVolumeBackend,
    IDisposable
{
    private readonly string runtimePath;
    private LibVLC? libVlc;
    private MediaPlayer? mediaPlayer;
    private VlcMedia? currentMedia;
    private object? attachedHost;
    private string? warningMessage;
    private bool isLoaded;

    /// <summary>
    /// Gets the underlying LibVLCSharp MediaPlayer instance for VideoView binding.
    /// Returns null until <see cref="OpenAsync"/> successfully initializes the player.
    /// </summary>
    public MediaPlayer? Player => mediaPlayer;

    public LibVlcPlaybackBackend(string runtimePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePath);
        this.runtimePath = runtimePath;
    }

    public bool TryAttachHost(object host)
    {
        attachedHost = host;
        if (mediaPlayer is not null)
        {
            BindPlayerToHost();
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

        try
        {
            Core.Initialize(runtimePath);
            libVlc = new LibVLC();
            mediaPlayer = new MediaPlayer(libVlc);
            BindPlayerToHost();

            currentMedia = new VlcMedia(libVlc, source.SourcePath, FromType.FromPath);
            MediaParsedStatus parseResult = await currentMedia.Parse(MediaParseOptions.ParseLocal).ConfigureAwait(false);

            if (parseResult != MediaParsedStatus.Done)
            {
                warningMessage = "VLC failed to parse the media file.";
                isLoaded = false;
                ReleaseMediaResources();
                return;
            }

            mediaPlayer.Media = currentMedia;
            isLoaded = true;
            warningMessage = null;
        }
        catch (Exception ex)
        {
            warningMessage = $"LibVLC initialization failed: {ex.Message}";
            isLoaded = false;
            ReleaseMediaResources();
        }
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

    public void AddSubtitleSidecar(string srtFilePath)
    {
        if (mediaPlayer is null || !File.Exists(srtFilePath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(srtFilePath);
        string subtitleUri = new Uri(fullPath, UriKind.Absolute).AbsoluteUri;
        mediaPlayer.AddSlave(MediaSlaveType.Subtitle, subtitleUri, true);
    }

    public int GetSubtitleTrackCount()
    {
        return mediaPlayer?.SpuCount ?? 0;
    }

    public int GetFirstSubtitleTrackId()
    {
        var descriptions = mediaPlayer?.SpuDescription;
        if (descriptions is null)
        {
            return -1;
        }

        foreach (var desc in descriptions)
        {
            if (desc.Id >= 0)
            {
                return desc.Id;
            }
        }

        return -1;
    }

    public void SetSubtitleTrack(int trackIndex)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.SetSpu(trackIndex);
        }
    }

    public void DisableSubtitles()
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.SetSpu(-1);
        }
    }

    public void Dispose()
    {
        ReleaseMediaResources();
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
        isLoaded = false;
    }

    private void BindPlayerToHost()
    {
        // Binding is platform-specific; the Avalonia VideoView handles this
        // through its MediaPlayer property assignment in the view layer.
    }
}
