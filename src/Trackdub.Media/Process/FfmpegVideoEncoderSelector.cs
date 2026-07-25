using Trackdub.Contracts;

namespace Trackdub.Media.Process;

internal static class FfmpegVideoEncoderSelector
{
    private const string SoftwareEncoder = "libx264";
    private const string HwDownloadFilterPrefix = "hwdownload,format=yuv420p,";

    public static VideoEncodeProfile Resolve(
        VideoEncoderPreference preference,
        FfmpegVideoEncoderSnapshot snapshot,
        MediaGpuHint? gpuHint = null)
    {
        if (preference == VideoEncoderPreference.Software)
        {
            return CreateSoftwareProfile(usedFallback: false, message: null);
        }

        if (preference != VideoEncoderPreference.Auto)
        {
            return ResolveForced(preference, snapshot);
        }

        return ResolveAuto(snapshot, gpuHint);
    }

    private static VideoEncodeProfile ResolveAuto(FfmpegVideoEncoderSnapshot snapshot, MediaGpuHint? hint)
    {
        if (hint is null or { HasGpu: false })
        {
            return CreateSoftwareProfile(
                usedFallback: true,
                message: "No GPU detected; using software libx264.");
        }

        foreach (VideoEncoderPreference candidate in GetAutoPreferenceOrder(hint))
        {
            VideoEncodeProfile? profile = TryResolveAutoCandidate(candidate, snapshot);
            if (profile is not null)
            {
                return profile;
            }
        }

        return CreateSoftwareProfile(
            usedFallback: true,
            message: "No hardware H.264 encoder is available in this FFmpeg build; using software libx264.");
    }

    private static IReadOnlyList<VideoEncoderPreference> GetAutoPreferenceOrder(MediaGpuHint? hint)
    {
        if (OperatingSystem.IsWindows())
        {
            return hint?.Vendor switch
            {
                GpuVendorKind.Intel => [VideoEncoderPreference.Qsv, VideoEncoderPreference.Nvenc, VideoEncoderPreference.Amf],
                GpuVendorKind.Amd => [VideoEncoderPreference.Amf, VideoEncoderPreference.Nvenc, VideoEncoderPreference.Qsv],
                GpuVendorKind.Nvidia => [VideoEncoderPreference.Nvenc, VideoEncoderPreference.Qsv, VideoEncoderPreference.Amf],
                _ => [VideoEncoderPreference.Nvenc, VideoEncoderPreference.Qsv, VideoEncoderPreference.Amf]
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return [VideoEncoderPreference.VideoToolbox];
        }

        if (OperatingSystem.IsLinux())
        {
            return hint?.Vendor == GpuVendorKind.Nvidia
                ? [VideoEncoderPreference.Nvenc, VideoEncoderPreference.Vaapi]
                : [VideoEncoderPreference.Vaapi, VideoEncoderPreference.Nvenc];
        }

        return [];
    }

    private static VideoEncodeProfile? TryResolveAutoCandidate(
        VideoEncoderPreference preference,
        FfmpegVideoEncoderSnapshot snapshot)
    {
        return preference switch
        {
            VideoEncoderPreference.Nvenc when snapshot.HasEncoder("h264_nvenc") =>
                CreateHardwareProfile(
                    "h264_nvenc",
                    BuildNvencArguments(),
                    BuildNvencInputHwaccel(snapshot),
                    usedFallback: false,
                    message: null),
            VideoEncoderPreference.Qsv when snapshot.HasEncoder("h264_qsv") =>
                CreateHardwareProfile(
                    "h264_qsv",
                    BuildQsvArguments(),
                    BuildWindowsHwaccel(snapshot),
                    usedFallback: false,
                    message: null),
            VideoEncoderPreference.Amf when snapshot.HasEncoder("h264_amf") =>
                CreateHardwareProfile(
                    "h264_amf",
                    BuildAmfArguments(),
                    BuildWindowsHwaccel(snapshot),
                    usedFallback: false,
                    message: null),
            VideoEncoderPreference.VideoToolbox when snapshot.HasEncoder("h264_videotoolbox") =>
                CreateHardwareProfile(
                    "h264_videotoolbox",
                    BuildVideoToolboxArguments(),
                    BuildMacOsHwaccel(snapshot),
                    usedFallback: false,
                    message: null),
            VideoEncoderPreference.Vaapi when snapshot.HasEncoder("h264_vaapi") =>
                CreateHardwareProfile(
                    "h264_vaapi",
                    BuildVaapiArguments(),
                    BuildLinuxHwaccel(snapshot),
                    usedFallback: false,
                    message: null),
            _ => null
        };
    }

    private static VideoEncodeProfile ResolveForced(
        VideoEncoderPreference preference,
        FfmpegVideoEncoderSnapshot snapshot)
    {
        (string encoder, IReadOnlyList<string> args, Func<FfmpegVideoEncoderSnapshot, IReadOnlyList<string>?>? hwaccelBuilder) mapping =
            preference switch
            {
                VideoEncoderPreference.Nvenc => ("h264_nvenc", BuildNvencArguments(), BuildNvencInputHwaccel),
                VideoEncoderPreference.Qsv => ("h264_qsv", BuildQsvArguments(), BuildWindowsHwaccel),
                VideoEncoderPreference.Amf => ("h264_amf", BuildAmfArguments(), BuildWindowsHwaccel),
                VideoEncoderPreference.VideoToolbox => ("h264_videotoolbox", BuildVideoToolboxArguments(), BuildMacOsHwaccel),
                VideoEncoderPreference.Vaapi => ("h264_vaapi", BuildVaapiArguments(), BuildLinuxHwaccel),
                _ => (SoftwareEncoder, BuildSoftwareArguments(), static _ => null)
            };

        if (!snapshot.HasEncoder(mapping.encoder))
        {
            return CreateSoftwareProfile(
                usedFallback: true,
                message: $"Requested encoder '{mapping.encoder}' is not available; using software libx264.");
        }

        IReadOnlyList<string>? hwaccel = mapping.hwaccelBuilder?.Invoke(snapshot);
        return CreateHardwareProfile(
            mapping.encoder,
            mapping.args,
            hwaccel,
            usedFallback: false,
            message: null);
    }

    private static VideoEncodeProfile CreateSoftwareProfile(bool usedFallback, string? message) =>
        new(
            SoftwareEncoder,
            BuildSoftwareArguments(),
            InputHwaccelArguments: null,
            VideoFilterPrefix: null,
            UsedFallback: usedFallback,
            Message: message);

    private static VideoEncodeProfile CreateHardwareProfile(
        string encoderName,
        IReadOnlyList<string> encoderArguments,
        IReadOnlyList<string>? inputHwaccelArguments,
        bool usedFallback,
        string? message) =>
        new(
            encoderName,
            encoderArguments,
            inputHwaccelArguments,
            inputHwaccelArguments is null ? null : HwDownloadFilterPrefix,
            usedFallback,
            message);

    private static IReadOnlyList<string> BuildSoftwareArguments() =>
        ["-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string> BuildNvencArguments() =>
        ["-preset", "p5", "-rc", "vbr", "-cq", "18", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string> BuildQsvArguments() =>
        ["-preset", "medium", "-global_quality", "18", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string> BuildAmfArguments() =>
        ["-quality", "balanced", "-rc", "cqp", "-qp_i", "18", "-qp_p", "18", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string> BuildVideoToolboxArguments() =>
        ["-q:v", "65", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string> BuildVaapiArguments() =>
        ["-qp", "18", "-pix_fmt", "yuv420p"];

    private static IReadOnlyList<string>? BuildWindowsHwaccel(FfmpegVideoEncoderSnapshot snapshot) =>
        snapshot.HasHwAccel("d3d11va")
            ? ["-hwaccel", "d3d11va"]
            : snapshot.HasHwAccel("dxva2")
                ? ["-hwaccel", "dxva2"]
                : null;

    private static IReadOnlyList<string>? BuildLinuxHwaccel(FfmpegVideoEncoderSnapshot snapshot) =>
        snapshot.HasHwAccel("vaapi") ? ["-hwaccel", "vaapi"] : null;

    private static IReadOnlyList<string>? BuildNvencInputHwaccel(FfmpegVideoEncoderSnapshot snapshot)
    {
        if (OperatingSystem.IsWindows())
        {
            return BuildWindowsHwaccel(snapshot);
        }

        if (OperatingSystem.IsLinux() && snapshot.HasHwAccel("cuda"))
        {
            return ["-hwaccel", "cuda"];
        }

        return null;
    }

    private static IReadOnlyList<string>? BuildMacOsHwaccel(FfmpegVideoEncoderSnapshot snapshot) =>
        snapshot.HasHwAccel("videotoolbox") ? ["-hwaccel", "videotoolbox"] : null;
}
