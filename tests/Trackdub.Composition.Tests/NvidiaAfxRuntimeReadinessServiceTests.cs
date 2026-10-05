using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxRuntimeReadinessServiceTests
{
    [Fact]
    public void GetReadiness_ReturnsMissingModels_WhenRuntimeInstalledWithoutProfileModels()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var logger = new TestLogger();
            var componentStore = new ComponentStore(tempRoot, logger);
            string runtimePath = componentStore.EnsureComponentDirectory(NvidiaAfxRuntimeDownloader.ComponentId);
            componentStore.MarkInstalled(NvidiaAfxRuntimeDownloader.ComponentId);
            File.WriteAllBytes(Path.Join(runtimePath, "NvAudioEffects.dll"), [0x00]);

            string manifestPath = Path.Join(tempRoot, "manifest.json");
            File.WriteAllText(manifestPath, """
            {
              "manifestVersion": "1.0.0",
              "packages": [
                {
                  "architecture": "ada",
                  "downloadUrl": "https://example.invalid",
                  "sha256": "0",
                  "sizeBytes": 1,
                  "runtimeVersion": "1",
                  "licenseUrl": "https://example.invalid/license",
                  "modelRelativePaths": [ "models/dereverb_denoiser_48k.nvam" ]
                }
              ]
            }
            """);

            // Settings outrank TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT, so this stays deterministic when a
            // developer has the variable set for the live GPU tests.
            var service = new NvidiaAfxRuntimeReadinessService(
                componentStore,
                new FixedArchitectureDetector("ada"),
                manifestPath,
                settingsProvider: () => StudioSettings.Default with { NvidiaAfxRuntimeDirectory = runtimePath });
            NvidiaAfxRuntimeReadiness readiness = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);

            Assert.False(readiness.IsReady);
            Assert.Equal("Missing model files", readiness.StatusLabel);
            Assert.Equal(runtimePath, readiness.RuntimeRoot);
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
    public void GetReadiness_CachesReadyResults_AndReportsTheArchitecture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe(succeeds: true);
        var service = fixture.CreateService(probe);

        NvidiaAfxRuntimeReadiness first = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);
        NvidiaAfxRuntimeReadiness second = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);

        Assert.True(first.IsReady);
        Assert.Equal("ada", first.ArchitectureBucket);
        Assert.Same(first, second);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public void GetReadiness_DoesNotCacheFailedProbes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = RuntimeFixture.Create();
        var probe = new CountingProbe(succeeds: false);
        var service = fixture.CreateService(probe);

        NvidiaAfxRuntimeReadiness first = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);
        NvidiaAfxRuntimeReadiness second = service.GetReadiness(NvidiaAfxProfile.NoiseAndReverb);

        Assert.False(first.IsReady);
        Assert.False(second.IsReady);
        Assert.Equal(2, probe.CallCount);
    }

    private sealed class CountingProbe(bool succeeds) : INvidiaAfxEffectProbe
    {
        public int CallCount { get; private set; }

        public NvidiaAfxEffectProbeResult Probe(
            string runtimeRoot,
            NvidiaAfxProfileDefinition profile,
            int inputSampleRate,
            string? architectureBucket = null)
        {
            CallCount++;
            return succeeds
                ? new NvidiaAfxEffectProbeResult(true, null, inputSampleRate)
                : new NvidiaAfxEffectProbeResult(false, "synthetic probe failure", null);
        }
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _tempRoot;
        private readonly string _runtimePath;
        private readonly string _manifestPath;

        private RuntimeFixture(string tempRoot, string runtimePath, string manifestPath)
        {
            _tempRoot = tempRoot;
            _runtimePath = runtimePath;
            _manifestPath = manifestPath;
        }

        public static RuntimeFixture Create()
        {
            string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-cache-{Guid.NewGuid():N}");
            string runtimePath = Path.Join(tempRoot, "runtime");
            Directory.CreateDirectory(Path.Join(runtimePath, "models"));
            File.WriteAllBytes(Path.Join(runtimePath, "NvAudioEffects.dll"), [0x00]);
            File.WriteAllText(Path.Join(runtimePath, "models", "dereverb_denoiser_16k.nvam"), "stub");
            File.WriteAllText(Path.Join(runtimePath, "models", "dereverb_denoiser_48k.nvam"), "stub");

            string manifestPath = Path.Join(tempRoot, "manifest.json");
            File.WriteAllText(manifestPath, """
            {
              "manifestVersion": "1.0.0",
              "packages": [
                {
                  "architecture": "ada",
                  "downloadUrl": "https://example.invalid",
                  "sha256": "0",
                  "sizeBytes": 1,
                  "runtimeVersion": "1",
                  "licenseUrl": "https://example.invalid/license",
                  "modelRelativePaths": [ "models/dereverb_denoiser_48k.nvam" ]
                }
              ]
            }
            """);
            return new RuntimeFixture(tempRoot, runtimePath, manifestPath);
        }

        public NvidiaAfxRuntimeReadinessService CreateService(INvidiaAfxEffectProbe probe) =>
            new(
                new ComponentStore(_tempRoot, new TestLogger()),
                new FixedArchitectureDetector("ada"),
                _manifestPath,
                settingsProvider: () => StudioSettings.Default with { NvidiaAfxRuntimeDirectory = _runtimePath },
                effectProbe: probe);

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
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

    private sealed class FixedArchitectureDetector(string bucket) : INvidiaAfxArchitectureDetector
    {
        public string DetectArchitectureBucket() => bucket;
    }
}
