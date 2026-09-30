using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxRuntimeInstallerTests
{
    [Fact]
    public async Task EnsureInstalledAsync_Refuses_WhileStubbed()
    {
        Assert.True(NvidiaAfxIntegration.IsStubbed());

        var logger = new NoopLogger();
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-installer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var componentStore = new Trackdub.Infrastructure.Components.ComponentStore(tempRoot, logger);
            using var httpClient = new HttpClient();
            var downloader = new NvidiaAfxRuntimeDownloader(componentStore, httpClient, logger);
            var settings = new FixedSettings(StudioSettings.Default with { NvidiaAfxLicenseAccepted = true });
            string manifestPath = Path.Join(tempRoot, "manifest.json");
            File.WriteAllText(manifestPath, """
            {
              "manifestVersion": "1.0.0",
              "packages": [
                {
                  "architecture": "ada",
                  "downloadUrl": "https://cdn.example.com/afx.zip",
                  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "sizeBytes": 1024,
                  "runtimeVersion": "1.0.0",
                  "licenseUrl": "https://example.com/license",
                  "modelRelativePaths": [ "models/denoiser_48k.nvam" ]
                }
              ]
            }
            """);

            var installer = new NvidiaAfxRuntimeInstaller(
                downloader,
                settings,
                new FixedArchitectureDetector("ada"),
                manifestPath);

            NvidiaAfxRuntimeInstallResult result = await installer.EnsureInstalledAsync(null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(NvidiaAfxIntegration.StubReason, result.FailureDetail);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FixedSettings(StudioSettings settings) : IStudioSettingsService
    {
        public Task<StudioSettings> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
        public Task SaveAsync(StudioSettings settingsToSave, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StudioSettings> TouchRecentProjectAsync(string projectPath, string projectName, CancellationToken cancellationToken) =>
            Task.FromResult(settings);
    }

    private sealed class FixedArchitectureDetector(string bucket) : INvidiaAfxArchitectureDetector
    {
        public string DetectArchitectureBucket() => bucket;
    }

    private sealed class NoopLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message, Exception? exception = null) { }
        public void LogError(string message, Exception? exception = null) { }
    }
}
