using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxStubContractTests
{
    [Fact]
    public void Integration_IsEnabled_AndKeepsItsKillSwitchMetadata()
    {
        Assert.False(NvidiaAfxIntegration.IsStubbed());
        Assert.Equal("nvidia-afx", NvidiaAfxIntegration.ProviderId);
        Assert.False(string.IsNullOrWhiteSpace(NvidiaAfxIntegration.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(NvidiaAfxIntegration.StubReason));
    }

    [Fact]
    public void ReadinessService_NeverReportsReady_WhenKillSwitchIsOn()
    {
        var logger = new NoopLogger();
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-stub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var componentStore = new Trackdub.Infrastructure.Components.ComponentStore(tempRoot, logger);
            var service = new NvidiaAfxRuntimeReadinessService(
                componentStore,
                new FixedArchitectureDetector("ada"),
                Path.Join(tempRoot, "missing-manifest.json"),
                isStubbed: static () => true);

            NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);

            Assert.False(readiness.IsReady);
            Assert.Equal(NvidiaAfxIntegration.StubStatusLabel, readiness.StatusLabel);
            Assert.Equal(NvidiaAfxIntegration.StubReason, readiness.FailureReason);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnhancementService_FallsBack_WhenKillSwitchIsOnEvenIfEnabledAndReadinessClaimsReady()
    {
        var fallback = new CapturingFallback();
        // Deliberately claim Ready to prove the kill switch short-circuits before native use.
        var readiness = new FakeReadinessService(
            new NvidiaAfxRuntimeReadiness(true, "Ready", @"C:\afx", null));
        var sut = new NvidiaAfxSpeechAudioEnhancementService(readiness, fallback)
        {
            IsStubbedOverride = static () => true
        };

        SpeechAudioEnhancementResult result = await sut.EnhanceAsync(
            new SpeechAudioEnhancementRequest(
                "source.wav",
                "dest.wav",
                new SpeechAudioEnhancementOptions(true, NvidiaAfxProfile.NoiseAndReverb, 1.0f)),
            CancellationToken.None);

        Assert.True(fallback.WasCalled);
        Assert.Equal(SpeechAudioEnhancementBackend.DeepFilterNet, result.Backend);
    }

    [Fact]
    public void ProfileCatalog_IsDiscoverable()
    {
        Assert.Equal(Enum.GetValues<NvidiaAfxProfile>().Length, NvidiaAfxProfileCatalog.Definitions.Count);

        foreach (NvidiaAfxProfileDefinition definition in NvidiaAfxProfileCatalog.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.Selector));
        }
    }

    [Fact]
    public async Task Downloader_RefusesInstall_WhenKillSwitchIsOn()
    {
        var logger = new NoopLogger();
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var componentStore = new Trackdub.Infrastructure.Components.ComponentStore(tempRoot, logger);
            using var httpClient = new HttpClient();
            var downloader = new NvidiaAfxRuntimeDownloader(
                componentStore,
                httpClient,
                logger,
                isStubbed: static () => true);
            var package = new NvidiaAfxRuntimePackage(
                Architecture: "ada",
                DownloadUrl: "https://example.invalid/afx.zip",
                Sha256: "0000000000000000000000000000000000000000000000000000000000000000",
                SizeBytes: 0,
                RuntimeVersion: "0.0.0",
                LicenseUrl: "https://example.invalid/license",
                ModelRelativePaths: ["models/denoiser_48k.nvam"]);

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                downloader.DownloadAndInstallAsync(package, progress: null, CancellationToken.None));

            Assert.Equal(NvidiaAfxIntegration.StubReason, ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FakeReadinessService(NvidiaAfxRuntimeReadiness readiness) : INvidiaAfxRuntimeReadinessService
    {
        public NvidiaAfxRuntimeReadiness GetReadiness(NvidiaAfxProfile profile) => readiness;
    }

    private sealed class CapturingFallback : ISpeechAudioEnhancementService
    {
        public bool WasCalled { get; private set; }

        public Task<SpeechAudioEnhancementResult> EnhanceAsync(
            SpeechAudioEnhancementRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(new SpeechAudioEnhancementResult(
                request.DestinationPath,
                1.0d,
                16000,
                1,
                16000,
                SpeechAudioEnhancementBackend.DeepFilterNet,
                null));
        }
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
