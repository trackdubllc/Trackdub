using System.Threading;
using Trackdub.Contracts;

namespace Trackdub.Media.Playback;

public sealed record MediaPlaybackRuntimeState(
    PlaybackVideoDecodePreference VideoDecodePreference = PlaybackVideoDecodePreference.Auto,
    FfmpegVideoEncoderSnapshot? FfmpegEncoderSnapshot = null,
    MediaGpuHint? GpuHint = null)
{
    public static MediaPlaybackRuntimeState Default { get; } = new();
}

/// <summary>
/// Process-wide playback options updated from studio settings (Avalonia shell).
/// </summary>
public sealed class PlaybackRuntimeOptions
{
    private MediaPlaybackRuntimeState state = MediaPlaybackRuntimeState.Default;

    public MediaPlaybackRuntimeState Snapshot => Volatile.Read(ref state);

    public void Apply(MediaPlaybackRuntimeState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Volatile.Write(ref state, next);
    }
}
