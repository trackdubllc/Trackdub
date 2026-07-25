using System.Runtime.CompilerServices;
using Trackdub.Inference.Onnx;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.HardwareProfiler;

/// <summary>
/// Skip gate for Opus translation benchmark smoke tests. Opus benchmarks require an encoder ONNX
/// (<c>encoder_model.onnx</c>) and an adjacent decoder (<c>decoder_model.onnx</c> or
/// <c>decoder_model_merged.onnx</c>) in the same directory before the test will run.
/// </summary>
internal static class TranslationBenchmarkGate
{
    internal static readonly IReadOnlyList<string> OpusCatalogAliases =
        ["opus-en-es", "helsinki-opus-en-es"];

    internal static string? ResolveSkipReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Windows-only translation benchmark smoke test (skipped on non-Windows test runs).";
        }

        if (IsOpusPresentFlagSet())
        {
            return null;
        }

        BenchmarkModelPathResolver resolver = BenchmarkModelPathResolver.CreateDefault();
        foreach (string alias in OpusCatalogAliases)
        {
            if (HasResolvableOnnx(resolver, alias))
            {
                return null;
            }
        }

        string? repoRoot = TestRepoRootResolver.TryFindRepoRoot();
        if (repoRoot is not null)
        {
            string bundledOpusRoot = Path.Combine(
                repoRoot,
                "models",
                "opus",
                "Helsinki-NLP-opus-mt-en-es");
            string bundledOpusEncoder = Path.Combine(bundledOpusRoot, "encoder_model.onnx");

            if (File.Exists(bundledOpusEncoder) && HasOpusDecoderAdjacent(bundledOpusEncoder))
            {
                return null;
            }
        }

        return "Opus translation ONNX not on disk (encoder_model.onnx plus decoder_model.onnx or decoder_model_merged.onnx). "
            + "Set TRACKDUB_OPUS_ONNX_PRESENT=1 when opus/helsinki ONNX is installed, or download models/opus/Helsinki-NLP-opus-mt-en-es.";
    }

    private static bool IsOpusPresentFlagSet() =>
        string.Equals(Environment.GetEnvironmentVariable("TRACKDUB_OPUS_ONNX_PRESENT"), "1", StringComparison.Ordinal);

    private static bool HasResolvableOnnx(BenchmarkModelPathResolver resolver, string alias)
    {
        BenchmarkModelResolutionResult resolution = resolver.Discover(alias);
        return resolution.Candidates.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate.ModelPath) &&
            File.Exists(candidate.ModelPath) &&
            HasOpusDecoderAdjacent(candidate.ModelPath));
    }

    /// <summary>
    /// Opus translation benchmarks load encoder and decoder sessions from the same model directory.
    /// </summary>
    private static bool HasOpusDecoderAdjacent(string encoderModelPath)
    {
        string modelDirectory = Path.GetDirectoryName(encoderModelPath)!;
        return File.Exists(Path.Combine(modelDirectory, "decoder_model.onnx")) ||
               File.Exists(Path.Combine(modelDirectory, "decoder_model_merged.onnx"));
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class TranslationOpusSmokeFactAttribute : FactAttribute
{
    public TranslationOpusSmokeFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        Skip = TranslationBenchmarkGate.ResolveSkipReason();
    }
}