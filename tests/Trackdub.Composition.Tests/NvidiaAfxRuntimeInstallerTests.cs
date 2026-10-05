using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxRuntimeInstallerTests
{
    [Fact]
    public async Task EnsureInstalledAsync_Refuses_WhenKillSwitchIsOn()
    {
        await using var harness = await InstallerHarness.CreateAsync(
            StudioSettings.Default with { NvidiaAfxLicenseAccepted = true },
            downloadableManifest: true,
            isStubbed: static () => true);

        NvidiaAfxRuntimeInstallResult result = await harness.Installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(NvidiaAfxIntegration.StubReason, result.FailureDetail);
    }

    [Fact]
    public async Task EnsureInstalledAsync_PastStub_Refuses_WhenLicenseNotAccepted()
    {
        await using var harness = await InstallerHarness.CreateAsync(
            StudioSettings.Default with { NvidiaAfxLicenseAccepted = false },
            downloadableManifest: true,
            isStubbed: static () => false);

        NvidiaAfxRuntimeInstallResult result = await harness.Installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("license", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureInstalledAsync_PastStub_Refuses_WhenNoDownloadablePackages()
    {
        await using var harness = await InstallerHarness.CreateAsync(
            StudioSettings.Default with { NvidiaAfxLicenseAccepted = true },
            downloadableManifest: false,
            isStubbed: static () => false);

        NvidiaAfxRuntimeInstallResult result = await harness.Installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("downloadable", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureInstalledAsync_PastStub_Refuses_WhenArchitectureMissing()
    {
        await using var harness = await InstallerHarness.CreateAsync(
            StudioSettings.Default with { NvidiaAfxLicenseAccepted = true },
            downloadableManifest: true,
            architecture: "hopper",
            isStubbed: static () => false);

        NvidiaAfxRuntimeInstallResult result = await harness.Installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("hopper", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureInstalledAsync_PastStub_Refuses_WhenManifestMissing()
    {
        await using var harness = await InstallerHarness.CreateAsync(
            StudioSettings.Default with { NvidiaAfxLicenseAccepted = true },
            downloadableManifest: true,
            isStubbed: static () => false,
            deleteManifest: true);

        NvidiaAfxRuntimeInstallResult result = await harness.Installer.EnsureInstalledAsync(null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("manifest", result.FailureDetail, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InstallerHarness : IAsyncDisposable
    {
        private readonly string _tempRoot;
        private readonly HttpClient _httpClient;

        private InstallerHarness(string tempRoot, HttpClient httpClient, NvidiaAfxRuntimeInstaller installer)
        {
            _tempRoot = tempRoot;
            _httpClient = httpClient;
            Installer = installer;
        }

        public NvidiaAfxRuntimeInstaller Installer { get; }

        public static Task<InstallerHarness> CreateAsync(
            StudioSettings settings,
            bool downloadableManifest,
            string architecture = "ada",
            Func<bool>? isStubbed = null,
            bool deleteManifest = false)
        {
            var logger = new NoopLogger();
            string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);

            var componentStore = new Trackdub.Infrastructure.Components.ComponentStore(tempRoot, logger);
            var httpClient = new HttpClient();
            var downloader = new NvidiaAfxRuntimeDownloader(componentStore, httpClient, logger);
            var settingsService = new FixedSettings(settings);
            string manifestPath = Path.Join(tempRoot, "manifest.json");
            File.WriteAllText(
                manifestPath,
                downloadableManifest
                    ? """
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
                    """
                    : """
                    {
                      "manifestVersion": "1.0.0",
                      "packages": [
                        {
                          "architecture": "ada",
                          "downloadUrl": "https://example.invalid/afx.zip",
                          "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                          "sizeBytes": 0,
                          "runtimeVersion": "0.0.0",
                          "licenseUrl": "https://example.com/license",
                          "modelRelativePaths": [ "models/denoiser_48k.nvam" ]
                        }
                      ]
                    }
                    """);

            if (deleteManifest)
            {
                File.Delete(manifestPath);
            }

            var installer = new NvidiaAfxRuntimeInstaller(
                downloader,
                settingsService,
                new FixedArchitectureDetector(architecture),
                manifestPath,
                isStubbed);

            return Task.FromResult(new InstallerHarness(tempRoot, httpClient, installer));
        }

        public ValueTask DisposeAsync()
        {
            _httpClient.Dispose();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
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
