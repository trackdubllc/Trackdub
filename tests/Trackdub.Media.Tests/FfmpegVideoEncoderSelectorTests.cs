using Trackdub.Contracts;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegVideoEncoderSelectorTests
{
    [Fact]
    public void Resolve_Auto_with_nvenc_selects_nvenc_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_nvenc");

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Nvidia));

        Assert.Equal("h264_nvenc", profile.EncoderName);
        Assert.False(profile.UsedFallback);
    }

    [Fact]
    public void Resolve_Auto_with_videotoolbox_selects_videotoolbox_on_macos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_videotoolbox");

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Apple));

        Assert.Equal("h264_videotoolbox", profile.EncoderName);
        Assert.False(profile.UsedFallback);
    }

    [Fact]
    public void Resolve_Auto_without_gpu_uses_software_immediately()
    {
        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_nvenc");

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: false));

        Assert.Equal("libx264", profile.EncoderName);
        Assert.True(profile.UsedFallback);
        Assert.Contains("No GPU detected", profile.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_Auto_without_hardware_falls_back_to_libx264()
    {
        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            FfmpegVideoEncoderSnapshot.Empty);

        Assert.Equal("libx264", profile.EncoderName);
        Assert.True(profile.UsedFallback);
        Assert.NotNull(profile.Message);
    }

    [Fact]
    public void Resolve_Software_always_uses_libx264()
    {
        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_nvenc");

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Software,
            snapshot);

        Assert.Equal("libx264", profile.EncoderName);
        Assert.False(profile.UsedFallback);
    }

    [Fact]
    public void Resolve_Auto_with_intel_gpu_prefers_qsv_on_windows_when_both_available()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_nvenc", "h264_qsv");
        MediaGpuHint hint = new(HasGpu: true, GpuVendorKind.Intel);

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            hint);

        Assert.Equal("h264_qsv", profile.EncoderName);
        Assert.False(profile.UsedFallback);
    }

    [Fact]
    public void Resolve_Auto_nvenc_on_linux_uses_cuda_not_vaapi_hwaccel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = new(
            new HashSet<string>(["h264_nvenc"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(["vaapi", "cuda"], StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Nvidia));

        Assert.Equal("h264_nvenc", profile.EncoderName);
        Assert.NotNull(profile.InputHwaccelArguments);
        Assert.Contains("cuda", profile.InputHwaccelArguments, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("vaapi", profile.InputHwaccelArguments, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_Auto_with_nvidia_gpu_prefers_nvenc_on_linux_when_both_available()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithEncoders("h264_vaapi", "h264_nvenc");
        MediaGpuHint hint = new(HasGpu: true, GpuVendorKind.Nvidia);

        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Auto,
            snapshot,
            hint);

        Assert.Equal("h264_nvenc", profile.EncoderName);
        Assert.False(profile.UsedFallback);
    }

    [Fact]
    public void Resolve_forced_nvenc_when_missing_falls_back()
    {
        VideoEncodeProfile profile = FfmpegVideoEncoderSelector.Resolve(
            VideoEncoderPreference.Nvenc,
            FfmpegVideoEncoderSnapshot.Empty);

        Assert.Equal("libx264", profile.EncoderName);
        Assert.True(profile.UsedFallback);
    }

    private static FfmpegVideoEncoderSnapshot SnapshotWithEncoders(params string[] encoders) =>
        new(
            new HashSet<string>(encoders, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);
}
