using Trackdub.Application.Runtime;
using Trackdub.Composition.Runtime;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Runtime.TrtRtxEp;
using Trackdub.Infrastructure.Settings;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class TrtRtxEpBundleInstallerLicenseTests
{
    [Fact]
    public async Task EnsureBundleAsync_WithoutLicenseConsent_ReturnsFailureBeforeDownload()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var settingsService = new FakeStudioSettingsService();
        await settingsService.SaveAsync(
            StudioSettings.Default with { NvidiaTensorRtRtxLicenseAccepted = false },
            CancellationToken.None);
        string tempRoot = Path.Combine(Path.GetTempPath(), $"trackdub-trt-rtx-license-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var installer = new TrtRtxEpBundleInstaller(
                new TrackdubStoragePaths(tempRoot),
                new TrtRtxEpBundleDownloader(new HttpClient(), new TestLogger()),
                settingsService,
                new TestLogger());

            TrtRtxEpBundleInstallResult result = await installer.EnsureBundleAsync(
                new Progress<string>(_ => { }),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.InstallDirectory);
            Assert.Contains("license", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class TestLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message, Exception? exception = null) { }
        public void LogError(string message, Exception? exception = null) { }
    }
}
