using Trackdub.Contracts;

namespace Trackdub.Media.Playback;

public static class LibMpvPlaybackOptions
{
    public static string ResolveHwdecOption(
        PlaybackVideoDecodePreference preference,
        FfmpegVideoEncoderSnapshot? ffmpegSnapshot = null,
        MediaGpuHint? gpuHint = null)
    {
        if (preference == PlaybackVideoDecodePreference.Software)
        {
            return "no";
        }

        if (gpuHint is { HasGpu: false })
        {
            return "no";
        }

        if (ffmpegSnapshot is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                if (ffmpegSnapshot.HasHwAccel("d3d11va"))
                {
                    return "d3d11va";
                }

                if (ffmpegSnapshot.HasHwAccel("dxva2"))
                {
                    return "dxva2";
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (ffmpegSnapshot.HasHwAccel("vaapi"))
                {
                    return "vaapi";
                }

                if (ffmpegSnapshot.HasHwAccel("cuda"))
                {
                    return "cuda";
                }
            }
            else if (OperatingSystem.IsMacOS() && ffmpegSnapshot.HasHwAccel("videotoolbox"))
            {
                return "videotoolbox";
            }
        }

        return "auto-safe";
    }
}
