using Trackdub.Contracts;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class LibMpvHwdecOptionTests
{
    // The composited backend renders through libmpv's software render API, so every hardware
    // selection must copy decoded frames back to system memory. A non-copy-back mode leaves the
    // SW render buffer zeroed (black video) even though the backend reports success.
    [Theory]
    [InlineData(PlaybackVideoDecodePreference.Auto, "auto-copy")]
    [InlineData(PlaybackVideoDecodePreference.Software, "no")]
    public void ResolveHwdecOption_without_probe_uses_software_render_safe_defaults(
        PlaybackVideoDecodePreference preference,
        string expected)
    {
        string actual = LibMpvPlaybackOptions.ResolveHwdecOption(preference);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ResolveHwdecOption_without_gpu_uses_software_decode()
    {
        string actual = LibMpvPlaybackOptions.ResolveHwdecOption(
            PlaybackVideoDecodePreference.Auto,
            FfmpegVideoEncoderSnapshot.Empty,
            new MediaGpuHint(HasGpu: false));

        Assert.Equal("no", actual);
    }

    [Fact]
    public void ResolveHwdecOption_with_vaapi_hwaccel_uses_vaapi_on_linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithHwAccels("vaapi");
        string actual = LibMpvPlaybackOptions.ResolveHwdecOption(
            PlaybackVideoDecodePreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Amd));

        Assert.Equal("vaapi-copy", actual);
    }

    [Fact]
    public void ResolveHwdecOption_with_d3d11va_hwaccel_uses_copy_back_d3d11va_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithHwAccels("d3d11va");
        string actual = LibMpvPlaybackOptions.ResolveHwdecOption(
            PlaybackVideoDecodePreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Nvidia));

        Assert.Equal("d3d11va-copy", actual);
    }

    [Fact]
    public void ResolveHwdecOption_never_returns_non_copy_back_hardware_mode()
    {
        FfmpegVideoEncoderSnapshot snapshot = SnapshotWithHwAccels(
            "d3d11va", "dxva2", "vaapi", "cuda", "videotoolbox");

        string actual = LibMpvPlaybackOptions.ResolveHwdecOption(
            PlaybackVideoDecodePreference.Auto,
            snapshot,
            new MediaGpuHint(HasGpu: true, GpuVendorKind.Nvidia));

        Assert.True(
            actual is "no" or "auto-copy" || actual.EndsWith("-copy", StringComparison.Ordinal),
            $"Expected a software or copy-back hwdec mode for the SW render target, got '{actual}'.");
    }

    private static FfmpegVideoEncoderSnapshot SnapshotWithHwAccels(params string[] hwaccels) =>
        new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(hwaccels, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);
}
