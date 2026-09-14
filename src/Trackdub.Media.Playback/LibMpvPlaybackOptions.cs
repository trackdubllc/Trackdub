using Trackdub.Contracts;

namespace Trackdub.Media.Playback;

public static class LibMpvPlaybackOptions
{
    // Every hwdec value returned here must be a copy-back mode: the only consumer is the
    // composited backend, which renders through libmpv's software render API
    // (MPV_RENDER_API_TYPE_SW) into a CPU buffer. Non-copy-back decoders keep frames
    // GPU-resident, so the software renderer produces an all-zero (black) buffer instead
    // of real pixels.
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
                    return "d3d11va-copy";
                }

                if (ffmpegSnapshot.HasHwAccel("dxva2"))
                {
                    return "dxva2-copy";
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (ffmpegSnapshot.HasHwAccel("vaapi"))
                {
                    return "vaapi-copy";
                }

                if (ffmpegSnapshot.HasHwAccel("cuda"))
                {
                    return "cuda-copy";
                }
            }
            else if (OperatingSystem.IsMacOS() && ffmpegSnapshot.HasHwAccel("videotoolbox"))
            {
                return "videotoolbox-copy";
            }
        }

        return "auto-copy";
    }
}
