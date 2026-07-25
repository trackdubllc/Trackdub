using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Tests;

public sealed class BenchmarkModelPathResolverTests
{
    [Fact]
    public void Discover_when_manifest_alias_has_no_onnx_file_returns_missing_entry_error()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string manifestDirectory = Path.Combine(tempDirectory, "manifest");
            Directory.CreateDirectory(manifestDirectory);
            string manifestPath = Path.Combine(manifestDirectory, "bundled-models.manifest.json");
            File.WriteAllText(
                manifestPath,
                """
                {
                  "models": [
                    {
                      "model_id": "example/temp-diarizer",
                      "task": "diarization",
                      "engine_family": "sortformer",
                      "license": "non-commercial",
                      "commercial_allowed": false,
                      "redistribution_allowed": true,
                      "requires_attribution": true,
                      "requires_user_consent": false,
                      "voice_cloning": false,
                      "commercial_safe_mode": false,
                      "source_url": "https://example.invalid/temp-diarizer",
                      "revision": "pending-export",
                      "sha256": "",
                      "aliases": [ "temp-diarizer" ],
                      "root_path": "../models/temp-diarizer",
                      "benchmark_entry": "onnx/model.onnx",
                      "variants": []
                    }
                  ]
                }
                """);

            BundledModelManifestRegistry registry = BundledModelManifestRegistry.Load(manifestPath);
            var resolver = new BenchmarkModelPathResolver(registry);

            BenchmarkModelResolutionResult result = resolver.Discover("temp-diarizer");

            Assert.Empty(result.Candidates);
            Assert.Equal("manifest:temp-diarizer", result.ScopeKey);
            Assert.Contains("no ONNX entry point exists on disk", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<FileNotFoundException>(() => resolver.ResolveSingle("temp-diarizer"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Discover_when_bundled_root_missing_uses_model_cache_directory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string manifestDirectory = Path.Combine(tempDirectory, "manifest");
            Directory.CreateDirectory(manifestDirectory);
            string manifestPath = Path.Combine(manifestDirectory, "bundled-models.manifest.json");
            File.WriteAllText(
                manifestPath,
                """
                {
                  "models": [
                    {
                      "model_id": "example/kokoro-cache",
                      "task": "tts",
                      "engine_family": "kokoro",
                      "license": "Apache-2.0",
                      "commercial_allowed": true,
                      "redistribution_allowed": true,
                      "requires_attribution": true,
                      "requires_user_consent": false,
                      "voice_cloning": false,
                      "commercial_use_verified": false,
                      "source_url": "https://example.invalid/kokoro-cache",
                      "revision": "cache-installed",
                      "sha256": "",
                      "aliases": [ "kokoro-cache" ],
                      "root_path": "../models/kokoro-cache",
                      "benchmark_entry": "onnx/model.onnx",
                      "variants": []
                    }
                  ]
                }
                """);

            string cacheRoot = Path.Combine(tempDirectory, "model-cache", "example", "kokoro-cache", "onnx");
            Directory.CreateDirectory(cacheRoot);
            string modelPath = Path.Combine(cacheRoot, "model.onnx");
            File.WriteAllText(modelPath, "fake-onnx");

            BundledModelManifestRegistry registry = BundledModelManifestRegistry.Load(manifestPath);
            var resolver = new BenchmarkModelPathResolver(
                registry,
                Path.Combine(tempDirectory, "model-cache"));

            BenchmarkModelResolutionResult result = resolver.Discover("kokoro-cache");
            BenchmarkModelCandidate candidate = resolver.ResolveSingle("kokoro-cache");

            Assert.Equal("cache:kokoro-cache", result.ScopeKey);
            Assert.Single(result.Candidates);
            Assert.Equal(modelPath, candidate.ModelPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
