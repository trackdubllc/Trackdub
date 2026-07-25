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

        string tempRoot = Path.Combine(Path.GetTempPath(), $"trackdub-afx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var logger = new TestLogger();
            var componentStore = new ComponentStore(tempRoot, logger);
            string runtimePath = componentStore.EnsureComponentDirectory(NvidiaAfxRuntimeDownloader.ComponentId);
            componentStore.MarkInstalled(NvidiaAfxRuntimeDownloader.ComponentId);

            string manifestPath = Path.Combine(tempRoot, "manifest.json");
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

            var service = new NvidiaAfxRuntimeReadinessService(componentStore, new FixedArchitectureDetector("ada"), manifestPath);
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
