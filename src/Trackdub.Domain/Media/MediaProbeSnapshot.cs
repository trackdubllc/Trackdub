namespace Trackdub.Domain.Media;

public sealed record MediaProbeSnapshot(
    string FormatName,
    string? FormatLongName,
    double DurationSeconds,
    long? BitRate,
    IReadOnlyList<MediaAudioStream> AudioStreams,
    IReadOnlyList<MediaVideoStream> VideoStreams,
    IReadOnlyList<MediaSubtitleStream>? SubtitleStreams = null);

public sealed record MediaAudioStream(
    int Index,
    string CodecName,
    int Channels,
    int SampleRate,
    double DurationSeconds);

public sealed record MediaVideoStream(
    int Index,
    string CodecName,
    int Width,
    int Height,
    double FrameRate,
    double DurationSeconds,
    string? PixelFormat = null,
    string? ColorSpace = null,
    string? ColorTransfer = null,
    string? ColorPrimaries = null);

public sealed record MediaSubtitleStream(
    int Index,
    string CodecName,
    string? Language);
