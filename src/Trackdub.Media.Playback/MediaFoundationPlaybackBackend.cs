#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Trackdub.Media.Playback;

public sealed class MediaFoundationPlaybackBackend :
    IPlaybackBackend,
    IPlaybackHostAwareBackend,
    IPlaybackRateBackend,
    IPlaybackVolumeBackend,
    IDisposable
{
    private MediaPlayer? mediaPlayer;
    private MediaPlayerElement? mediaPlayerElement;
    private bool isLoaded;
    private string? runtimeWarningMessage;

    public bool TryAttachHost(object host)
    {
        if (host is not MediaPlayerElement element)
        {
            return false;
        }

        mediaPlayerElement = element;
        if (mediaPlayer is not null)
        {
            mediaPlayerElement.SetMediaPlayer(mediaPlayer);
        }

        return true;
    }

    public async Task OpenAsync(MediaSourceDescriptor source, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourcePath);

        MediaPlayer player = EnsurePlayer();
        isLoaded = false;
        runtimeWarningMessage = null;

        // We must await MediaOpened before returning so that a subsequent PlayAsync call
        // finds the player in a ready state. If Play() is called while the MediaPlayer is
        // still in the Opening state, Windows Media Foundation silently drops the call.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnMediaOpened(MediaPlayer sender, object args)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            tcs.TrySetResult(true);
        }

        void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            // Resolve rather than fault: the MediaFailed event is surfaced separately as a
            // warning through GetSnapshotAsync / PlaybackWarning. We don't want OpenAsync to
            // throw because the caller (PlaybackService) has already stored this backend and
            // the UI will display the format warning regardless.
            tcs.TrySetResult(false);
        }

        player.MediaOpened += OnMediaOpened;
        player.MediaFailed += OnMediaFailed;

        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            player.MediaOpened -= OnMediaOpened;
            player.MediaFailed -= OnMediaFailed;
            tcs.TrySetCanceled(ct);
        });

        // Build a well-formed URI from a Windows file path. new Uri(path, UriKind.Absolute)
        // handles both "C:\..." absolute paths and existing "file:///" URIs correctly.
        player.Source = null;
        player.Source = MediaSource.CreateFromUri(new Uri(source.SourcePath, UriKind.Absolute));
        player.PlaybackSession.PlaybackRate = 1d;
        player.Volume = 1d;

        await tcs.Task.ConfigureAwait(false);

        // Guarantee we start paused at position zero even if AutoPlay somehow fired.
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
        }

        player.PlaybackSession.Position = TimeSpan.Zero;
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
        if (mediaPlayer is not null)
        {
            mediaPlayer.PlaybackSession.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        }

        return Task.CompletedTask;
    }

    public Task SetPlaybackRateAsync(double playbackRate, CancellationToken ct)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.PlaybackSession.PlaybackRate = playbackRate <= 0d ? 1d : playbackRate;
        }

        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken ct)
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0d, 1d) : 1d;
        }

        return Task.CompletedTask;
    }

    public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (mediaPlayer is null)
        {
            return Task.FromResult(PlaybackSnapshot.Empty);
        }

        MediaPlaybackSession session = mediaPlayer.PlaybackSession;
        return Task.FromResult(new PlaybackSnapshot(
            IsLoaded: isLoaded,
            IsPlaying: session.PlaybackState == MediaPlaybackState.Playing,
            Position: session.Position,
            Duration: session.NaturalDuration,
            PlaybackRate: session.PlaybackRate,
            WarningMessage: runtimeWarningMessage));
    }

    public void Dispose()
    {
        if (mediaPlayer is not null)
        {
            mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
            mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
        }

        mediaPlayerElement?.SetMediaPlayer(null);
        mediaPlayer?.Dispose();
        mediaPlayer = null;
        isLoaded = false;
        runtimeWarningMessage = null;
    }

    private MediaPlayer EnsurePlayer()
    {
        if (mediaPlayer is not null)
        {
            return mediaPlayer;
        }

        mediaPlayer = new MediaPlayer
        {
            AutoPlay = false,
            IsLoopingEnabled = false
        };
        mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
        mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        if (mediaPlayerElement is not null)
        {
            mediaPlayerElement.SetMediaPlayer(mediaPlayer);
        }

        return mediaPlayer;
    }

    private void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
    {
        isLoaded = true;
        runtimeWarningMessage = null;
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        isLoaded = false;
        runtimeWarningMessage = BuildFailureWarning(args);
    }

    private static string BuildFailureWarning(MediaPlayerFailedEventArgs args) =>
        $"Media Foundation failed to open or play this source ({args.Error}).";
}
#endif
