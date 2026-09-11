using Trackdub.Inference.Onnx;

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
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveSingle_MapsNestedGenAiConfigFromCacheUsingManifestVariantPath()
    {
        Assert.True(
            Trackdub.Inference.Runtime.ModelManifest.BundledModelManifestRegistry.TryLoadDefault(
                out Trackdub.Inference.Runtime.ModelManifest.BundledModelManifestRegistry? registry,
                out string? error),
            error ?? "Bundled model manifest was not found.");

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
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
        }
    }
}
