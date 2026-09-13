using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class BenchmarkModelPathResolverTests
{
    [Theory]
    [InlineData("model_fp16.onnx", "fp16", true)]
    [InlineData("encoder_model_quantized.onnx", "quantized", true)]
    [InlineData("language_model_fp16.onnx", "fp16", true)]
    [InlineData("model.onnx", "fp16", false)]
    public void ResolveSingle_MatchesManifestVariantAliasesFromCachedFilenames(
        string fileName,
        string requestedVariant,
        bool shouldResolve)
    {
        string cacheRoot = Path.Combine(Path.GetTempPath(), $"trackdub-resolver-{Guid.NewGuid():N}");
        string modelDirectory = Path.Combine(cacheRoot, "onnx-community", "silero-vad", "onnx");
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllBytes(Path.Combine(modelDirectory, fileName), []);
        string modelScope = Path.Combine(cacheRoot, "onnx-community", "silero-vad");

        try
        {
            var resolver = new BenchmarkModelPathResolver(modelCacheDirectory: cacheRoot);
            if (shouldResolve)
            {
                BenchmarkModelCandidate candidate = resolver.ResolveSingle(modelScope, requestedVariant);
                Assert.EndsWith(fileName, candidate.ModelPath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Throws<FileNotFoundException>(() =>
                    resolver.ResolveSingle(modelScope, requestedVariant));
            }
        }
        finally
        {
            DeleteDirectory(cacheRoot);
        }
    }

    [Fact]
    public void ResolveSingle_MapsNestedGenAiConfigFromCacheUsingManifestVariantPath()
    {
        BundledModelManifestRegistry registry = LoadDefaultManifestRegistry();

        string cacheRoot = Path.Combine(Path.GetTempPath(), $"trackdub-phi-cache-{Guid.NewGuid():N}");
        string variantDirectory = Path.Combine(
            cacheRoot,
            "microsoft",
            "Phi-4-mini-instruct-onnx",
            "gpu",
            "gpu-int4-rtn-block-32");
        Directory.CreateDirectory(variantDirectory);
        string genAiConfigPath = Path.Combine(variantDirectory, "genai_config.json");
        File.WriteAllText(genAiConfigPath, "{}");

        try
        {
            var resolver = new BenchmarkModelPathResolver(registry, cacheRoot);
            BenchmarkModelCandidate candidate = resolver.ResolveSingle(
                "microsoft/Phi-4-mini-instruct-onnx",
                "gpu-int4");

            Assert.Equal("gpu-int4", candidate.VariantAlias, StringComparer.OrdinalIgnoreCase);
            Assert.True(
                candidate.ModelPath.Equals(genAiConfigPath, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(candidate.ModelPath).Equals("genai_config.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(cacheRoot);
        }
    }

    [Fact]
    public void ResolveSingle_ManifestKokoroRootDirectoryPrefersCacheRootWithVoices()
    {
        BundledModelManifestEntry kokoroTemplate = LoadDefaultManifestRegistry().Entries.Single(entry =>
            entry.ModelId.Equals("onnx-community/Kokoro-82M-v1.0-ONNX", StringComparison.OrdinalIgnoreCase));

        string cacheRoot = CreateKokoroCacheRoot(includeVoices: true);
        string manifestRoot = Path.Combine(Path.GetTempPath(), $"trackdub-kokoro-manifest-{Guid.NewGuid():N}");
        SeedKokoroModelLayout(manifestRoot, includeVoices: false);

        try
        {
            BundledModelManifestRegistry registry = CreateRelocatedKokoroRegistry(kokoroTemplate, manifestRoot);
            var resolver = new BenchmarkModelPathResolver(registry, cacheRoot);
            BenchmarkModelCandidate candidate = resolver.ResolveSingle("kokoro-onnx");

            AssertRootDirectoryContainsVoices(candidate);
            Assert.False(
                PathsEqual(candidate.RootDirectory, manifestRoot),
                "Manifest-relative repo models root must not win over the cache root that contains voices/.");
        }
        finally
        {
            DeleteDirectory(cacheRoot);
            DeleteDirectory(manifestRoot);
        }
    }

    [Fact]
    public void ResolveSingle_ManifestKokoroRootDirectoryFallsBackWhenCacheIsIncomplete()
    {
        BundledModelManifestEntry kokoroTemplate = LoadDefaultManifestRegistry().Entries.Single(entry =>
            entry.ModelId.Equals("onnx-community/Kokoro-82M-v1.0-ONNX", StringComparison.OrdinalIgnoreCase));

        string cacheRoot = Path.Combine(Path.GetTempPath(), $"trackdub-kokoro-empty-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(cacheRoot, "onnx-community", "Kokoro-82M-v1.0-ONNX"));
        string manifestRoot = Path.Combine(Path.GetTempPath(), $"trackdub-kokoro-repo-{Guid.NewGuid():N}");
        SeedKokoroModelLayout(manifestRoot, includeVoices: true);

        try
        {
            BundledModelManifestRegistry registry = CreateRelocatedKokoroRegistry(kokoroTemplate, manifestRoot);
            var resolver = new BenchmarkModelPathResolver(registry, cacheRoot);
            BenchmarkModelCandidate candidate = resolver.ResolveSingle("kokoro-onnx");

            AssertRootDirectoryContainsVoices(candidate);
            Assert.True(
                PathsEqual(candidate.RootDirectory, manifestRoot),
                "Incomplete cache root must not replace the repo models root that contains voices/.");
        }
        finally
        {
            DeleteDirectory(cacheRoot);
            DeleteDirectory(manifestRoot);
        }
    }

    [Fact]
    public void ResolveSingle_CachedKokoroRootDirectoryContainsVoices()
    {
        BundledModelManifestRegistry registry = LoadDefaultManifestRegistry();
        string cacheRoot = CreateKokoroCacheRoot(includeVoices: true);

        try
        {
            var resolver = new BenchmarkModelPathResolver(registry, cacheRoot);
            BenchmarkModelCandidate candidate = resolver.ResolveSingle("kokoro-onnx");
            AssertRootDirectoryContainsVoices(candidate);
        }
        finally
        {
            DeleteDirectory(cacheRoot);
        }
    }

    private static BundledModelManifestRegistry LoadDefaultManifestRegistry()
    {
        Assert.True(
            BundledModelManifestRegistry.TryLoadDefault(
                out BundledModelManifestRegistry? registry,
                out string? error),
            error ?? "Bundled model manifest was not found.");
        return registry!;
    }

    private static string CreateKokoroCacheRoot(bool includeVoices)
    {
        string cacheRoot = Path.Combine(Path.GetTempPath(), $"trackdub-kokoro-cache-{Guid.NewGuid():N}");
        string modelRoot = Path.Combine(cacheRoot, "onnx-community", "Kokoro-82M-v1.0-ONNX");
        SeedKokoroModelLayout(modelRoot, includeVoices);
        return cacheRoot;
    }

    private static void SeedKokoroModelLayout(string modelRoot, bool includeVoices)
    {
        string onnxDirectory = Path.Combine(modelRoot, "onnx");
        Directory.CreateDirectory(onnxDirectory);
        File.WriteAllBytes(Path.Combine(onnxDirectory, "model.onnx"), []);
        if (!includeVoices)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(modelRoot, "voices"));
        File.WriteAllBytes(Path.Combine(modelRoot, "voices", "af_heart.bin"), []);
    }

    private static BundledModelManifestRegistry CreateRelocatedKokoroRegistry(
        BundledModelManifestEntry template,
        string manifestRoot)
    {
        string relocatedRoot = Path.GetFullPath(manifestRoot);
        BundledModelManifestEntry relocated = template with
        {
            RootDirectory = relocatedRoot,
            DefaultBenchmarkEntryPath = Path.Combine(relocatedRoot, "onnx", "model.onnx"),
            Variants = [],
        };

        return BundledModelManifestRegistry.CreateForTests("test-kokoro-manifest", [relocated]);
    }

    private static void AssertRootDirectoryContainsVoices(BenchmarkModelCandidate candidate)
    {
        Assert.True(
            candidate.RootDirectory is not null &&
            Directory.Exists(Path.Combine(candidate.RootDirectory, "voices")),
            $"Expected Kokoro RootDirectory to contain voices/, got '{candidate.RootDirectory}'.");
    }

    private static bool PathsEqual(string? left, string right) =>
        left is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
