using Trackdub.Contracts;
using Trackdub.Media.Playback;

namespace Trackdub.Media.Tests;

public sealed class LibMpvHwdecOptionTests
{
    [Theory]
    [InlineData(PlaybackVideoDecodePreference.Auto, "auto-safe")]
    [InlineData(PlaybackVideoDecodePreference.Software, "no")]
    public void ResolveHwdecOption_without_probe_uses_legacy_defaults(
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

        Assert.Equal("vaapi", actual);
    }

    [Fact]
    public void ResolveHwdecOption_with_d3d11va_hwaccel_uses_d3d11va_on_windows()
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

        Assert.Equal("d3d11va", actual);
    }

    private static FfmpegVideoEncoderSnapshot SnapshotWithHwAccels(params string[] hwaccels) =>
        new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(hwaccels, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow);
}
