using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;
using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxInstalledRuntimeEvaluatorTests
{
    [Fact]
    public void Evaluate_FailsNativeProbe_EvenWhenDllAndModelsExist()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string runtimeRoot = Path.Join(tempRoot, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllBytes(Path.Join(runtimeRoot, "NvAudioEffects.dll"), [0x00]);
            Directory.CreateDirectory(Path.Join(runtimeRoot, "models"));
            File.WriteAllText(Path.Join(runtimeRoot, "models", "dereverb_denoiser_16k.nvam"), "stub");
            File.WriteAllText(Path.Join(runtimeRoot, "models", "dereverb_denoiser_48k.nvam"), "stub");

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
                  "modelRelativePaths": [
                    "models/dereverb_denoiser_16k.nvam",
                    "models/dereverb_denoiser_48k.nvam"
                  ]
                }
              ]
            }
            """);

            var evaluator = new NvidiaAfxInstalledRuntimeEvaluator(
                new FixedArchitectureDetector("ada"),
                manifestPath,
                new FailingProbe("synthetic probe failure"));

            NvidiaAfxRuntimeReadiness readiness = evaluator.Evaluate(
                NvidiaAfxProfile.NoiseAndReverb,
                runtimeRoot);

            Assert.False(readiness.IsReady);
            Assert.Equal("Native probe failed", readiness.StatusLabel);
            Assert.Contains("synthetic probe failure", readiness.FailureReason, StringComparison.Ordinal);
            Assert.Equal(runtimeRoot, readiness.RuntimeRoot);
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
    public void Evaluate_Fails_WhenMaxineFeatureDllMissing()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-eval-feat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string runtimeRoot = Path.Join(tempRoot, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllBytes(Path.Join(runtimeRoot, "NVAudioEffects.dll"), [0x00]);

            string modelDir = Path.Join(runtimeRoot, "features", "nvafxdereverbdenoiser", "models", "ada");
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Join(modelDir, "dereverb_denoiser_16k.trtpkg"), "stub");
            File.WriteAllText(Path.Join(modelDir, "dereverb_denoiser_48k.trtpkg"), "stub");
            // features/ exists but bin/ DLL is intentionally absent

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
                  "modelRelativePaths": [
                    "features/nvafxdereverbdenoiser/models/ada/dereverb_denoiser_16k.trtpkg",
                    "features/nvafxdereverbdenoiser/models/ada/dereverb_denoiser_48k.trtpkg"
                  ]
                }
              ]
            }
            """);

            var evaluator = new NvidiaAfxInstalledRuntimeEvaluator(
                new FixedArchitectureDetector("ada"),
                manifestPath,
                new SucceedingProbe(48000));

            NvidiaAfxRuntimeReadiness readiness = evaluator.Evaluate(
                NvidiaAfxProfile.NoiseAndReverb,
                runtimeRoot);

            Assert.False(readiness.IsReady);
            Assert.Equal("Missing feature libraries", readiness.StatusLabel);
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
    public void Evaluate_ReportsReady_OnlyAfterSuccessfulProbe()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), $"trackdub-afx-eval-ok-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string runtimeRoot = Path.Join(tempRoot, "runtime");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllBytes(Path.Join(runtimeRoot, "NvAudioEffects.dll"), [0x00]);
            Directory.CreateDirectory(Path.Join(runtimeRoot, "models"));
            File.WriteAllText(Path.Join(runtimeRoot, "models", "dereverb_denoiser_16k.nvam"), "stub");
            File.WriteAllText(Path.Join(runtimeRoot, "models", "dereverb_denoiser_48k.nvam"), "stub");

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
                  "modelRelativePaths": [
                    "models/dereverb_denoiser_16k.nvam",
                    "models/dereverb_denoiser_48k.nvam"
                  ]
                }
              ]
            }
            """);

            var evaluator = new NvidiaAfxInstalledRuntimeEvaluator(
                new FixedArchitectureDetector("ada"),
                manifestPath,
                new SucceedingProbe(48000));

            NvidiaAfxRuntimeReadiness readiness = evaluator.Evaluate(
                NvidiaAfxProfile.NoiseAndReverb,
                runtimeRoot);

            Assert.True(readiness.IsReady);
            Assert.Equal("Ready", readiness.StatusLabel);
            Assert.Equal(runtimeRoot, readiness.RuntimeRoot);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class FixedArchitectureDetector(string bucket) : INvidiaAfxArchitectureDetector
    {
        public string DetectArchitectureBucket() => bucket;
    }

    private sealed class FailingProbe(string reason) : INvidiaAfxEffectProbe
    {
        public NvidiaAfxEffectProbeResult Probe(
            string runtimeRoot,
            NvidiaAfxProfileDefinition profile,
            int inputSampleRate,
            string? architectureBucket = null) =>
            new(false, reason, null);
    }

    private sealed class SucceedingProbe(int outputSampleRate) : INvidiaAfxEffectProbe
    {
        public NvidiaAfxEffectProbeResult Probe(
            string runtimeRoot,
            NvidiaAfxProfileDefinition profile,
            int inputSampleRate,
            string? architectureBucket = null) =>
            new(true, null, outputSampleRate);
    }
}
