namespace Trackdub.Contracts;

public enum VideoEncoderPreference
{
    Auto = 0,
    Software = 1,
    Nvenc = 2,
    Qsv = 3,
    Amf = 4,
    VideoToolbox = 5,
    Vaapi = 6
}

public enum PlaybackVideoDecodePreference
{
    Auto = 0,
    Software = 1
}

public sealed record FfmpegVideoEncoderSnapshot(
    IReadOnlySet<string> Encoders,
    IReadOnlySet<string> HwAccels,
    DateTimeOffset ProbedAtUtc)
{
    public static FfmpegVideoEncoderSnapshot Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        DateTimeOffset.MinValue);

    public bool HasEncoder(string encoderId) =>
        Encoders.Contains(encoderId);

    public bool HasHwAccel(string hwaccelName) =>
        HwAccels.Contains(hwaccelName);
}

public sealed record VideoEncodeProfile(
    string EncoderName,
    IReadOnlyList<string> EncoderArguments,
    IReadOnlyList<string>? InputHwaccelArguments,
    string? VideoFilterPrefix,
    bool UsedFallback,
    string? Message);

public interface IFfmpegVideoEncoderCapabilities
{
    FfmpegVideoEncoderSnapshot GetSnapshot();

    Task<FfmpegVideoEncoderSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}

public enum GpuVendorKind
{
    Unknown = 0,
    Nvidia = 1,
    Intel = 2,
    Amd = 3,
    Apple = 4
}

public sealed record MediaGpuHint(bool HasGpu, GpuVendorKind Vendor = GpuVendorKind.Unknown);

public interface IMediaGpuHintProvider
{
    Task<MediaGpuHint> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed record MediaHardwareCapabilities(
    FfmpegVideoEncoderSnapshot FfmpegEncoders,
    MediaGpuHint GpuHint);

public interface IMediaHardwareCapabilitiesService
{
    Task<MediaHardwareCapabilities> RefreshAsync(CancellationToken cancellationToken = default);
}

public static class MediaGpuHints
{
    public static MediaGpuHint FromProfile(bool hasGpu, string? gpuDescription)
    {
        if (!hasGpu)
        {
            return new MediaGpuHint(false);
        }

        return new MediaGpuHint(true, ClassifyVendor(gpuDescription));
    }

    public static GpuVendorKind ClassifyVendor(string? gpuDescription)
    {
        if (string.IsNullOrWhiteSpace(gpuDescription))
        {
            return GpuVendorKind.Unknown;
        }

        string text = gpuDescription.ToLowerInvariant();
        if (text.Contains("nvidia", StringComparison.Ordinal) ||
            text.Contains("geforce", StringComparison.Ordinal) ||
            text.Contains("quadro", StringComparison.Ordinal) ||
            text.Contains("rtx ", StringComparison.Ordinal) ||
            text.Contains("gtx ", StringComparison.Ordinal))
        {
            return GpuVendorKind.Nvidia;
        }

        if (text.Contains("apple", StringComparison.Ordinal) ||
            text.Contains("m1", StringComparison.Ordinal) ||
            text.Contains("m2", StringComparison.Ordinal) ||
            text.Contains("m3", StringComparison.Ordinal) ||
            text.Contains("m4", StringComparison.Ordinal))
        {
            return GpuVendorKind.Apple;
        }

        if (text.Contains("amd", StringComparison.Ordinal) ||
            text.Contains("radeon", StringComparison.Ordinal))
        {
            return GpuVendorKind.Amd;
        }

        if (text.Contains("intel", StringComparison.Ordinal) ||
            text.Contains("iris", StringComparison.Ordinal) ||
            text.Contains("uhd graphics", StringComparison.Ordinal))
        {
            return GpuVendorKind.Intel;
        }

        return GpuVendorKind.Unknown;
    }
}

public static class FfmpegVideoEncoderCapabilitiesFormatter
{
    private static readonly string[] TrackedEncoders =
    [
        "h264_nvenc",
        "h264_qsv",
        "h264_amf",
        "h264_videotoolbox",
        "h264_vaapi",
        "libx264"
    ];

    private static readonly string[] TrackedHwAccels =
    [
        "d3d11va",
        "dxva2",
        "cuda",
        "vaapi",
        "videotoolbox"
    ];

    public static string FormatEncoders(FfmpegVideoEncoderSnapshot snapshot)
    {
        IReadOnlyList<string> found = TrackedEncoders
            .Where(snapshot.HasEncoder)
            .ToArray();

        return found.Count > 0
            ? string.Join(", ", found)
            : "none detected in FFmpeg";
    }

    public static string FormatHwAccels(FfmpegVideoEncoderSnapshot snapshot)
    {
        IReadOnlyList<string> found = TrackedHwAccels
            .Where(snapshot.HasHwAccel)
            .ToArray();

        return found.Count > 0
            ? string.Join(", ", found)
            : "none detected in FFmpeg";
    }
}

public static class VideoEncoderPreferenceSettings
{
    public const string AutoKey = "auto";
    public const string SoftwareKey = "software";
    public const string NvencKey = "nvenc";
    public const string QsvKey = "qsv";
    public const string AmfKey = "amf";
    public const string VideoToolboxKey = "videotoolbox";
    public const string VaapiKey = "vaapi";

    public static string ToKey(VideoEncoderPreference preference) =>
        preference switch
        {
            VideoEncoderPreference.Software => SoftwareKey,
            VideoEncoderPreference.Nvenc => NvencKey,
            VideoEncoderPreference.Qsv => QsvKey,
            VideoEncoderPreference.Amf => AmfKey,
            VideoEncoderPreference.VideoToolbox => VideoToolboxKey,
            VideoEncoderPreference.Vaapi => VaapiKey,
            _ => AutoKey
        };

    public static VideoEncoderPreference FromKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? VideoEncoderPreference.Auto
            : key.Trim().ToLowerInvariant() switch
            {
                SoftwareKey => VideoEncoderPreference.Software,
                NvencKey => VideoEncoderPreference.Nvenc,
                QsvKey => VideoEncoderPreference.Qsv,
                AmfKey => VideoEncoderPreference.Amf,
                VideoToolboxKey => VideoEncoderPreference.VideoToolbox,
                VaapiKey => VideoEncoderPreference.Vaapi,
                _ => VideoEncoderPreference.Auto
            };
}

public static class PlaybackVideoDecodePreferenceSettings
{
    public const string AutoKey = "auto";
    public const string SoftwareKey = "software";

    public static string ToKey(PlaybackVideoDecodePreference preference) =>
        preference == PlaybackVideoDecodePreference.Software
            ? SoftwareKey
            : AutoKey;

    public static PlaybackVideoDecodePreference FromKey(string? key) =>
        string.Equals(key?.Trim(), SoftwareKey, StringComparison.OrdinalIgnoreCase)
            ? PlaybackVideoDecodePreference.Software
            : PlaybackVideoDecodePreference.Auto;
}
