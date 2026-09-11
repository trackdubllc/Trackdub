using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.StarterPacks;

public enum TrtRtxStarterPackSmokeTargetStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record TrtRtxStarterPackSmokeTargetResult(
    string Label,
    string ModelReference,
    TrtRtxStarterPackSmokeTargetStatus Status,
    string? Detail = null);

public sealed record TrtRtxStarterPackSmokeReport(
    int Attempted,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TrtRtxStarterPackSmokeTargetResult> Targets)
{
    public bool HasFailures => Failed > 0;

    public bool HasAttempts => Attempted > 0;
}

/// <summary>
/// Runs planner-style ONNX smoke tests for starter-pack turbo TRT RTX targets.
/// </summary>
public static class TrtRtxStarterPackSmokeRunner
{
    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            new OnnxExecutionProviderSmokeTester(),
            cancellationToken);

    public static async Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IExecutionProviderSmokeTester smokeTester,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(smokeTester);

        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? manifestError))
        {
            throw new InvalidOperationException(manifestError ?? "Bundled model manifest was not found.");
        }

        var resolver = new BenchmarkModelPathResolver(registry, modelCacheDirectory);
        var results = new List<TrtRtxStarterPackSmokeTargetResult>(TrtRtxSmokeCatalog.StarterPackTurboGpu.Count);
        int attempted = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (TrtRtxSmokeCatalog.Target target in TrtRtxSmokeCatalog.StarterPackTurboGpu)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BenchmarkModelCandidate candidate;
            try
            {
                candidate = resolver.ResolveSingle(target.ModelReference, target.Variant);
            }
            catch (FileNotFoundException ex)
            {
                skipped++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Skipped,
                    ex.Message));
                continue;
            }

            BundledModelManifestEntry? entry = registry!.Entries.FirstOrDefault(
                model => model.ModelId.Equals(target.ModelReference, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                failed++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    $"Manifest entry missing for '{target.ModelReference}'."));
                continue;
            }

            string modelRootPath = ResolveSmokeModelRootPath(candidate);

            string variantAlias = target.Variant
                ?? candidate.VariantAlias
                ?? "default";

            RuntimeStage stage;
            try
            {
                stage = ResolveStage(target.Label);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                failed++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    ex.Message));
                continue;
            }

            var request = new ExecutionProviderSmokeTestRequest(
                stage,
                entry.ModelId,
                entry.Aliases[0],
                entry.EngineFamily,
                variantAlias,
                ExecutionProviderKind.TensorRTRtx,
                modelRootPath,
                candidate.ModelPath);

            attempted++;
            ExecutionProviderSmokeTestResult smokeResult = await smokeTester
                .SmokeTestAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (smokeResult.Passed)
            {
                passed++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Passed));
            }
            else
            {
                failed++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    smokeResult.Detail ?? "smoke test failed"));
            }
        }

        return new TrtRtxStarterPackSmokeReport(attempted, passed, failed, skipped, results);
    }

    private static RuntimeStage ResolveStage(string label) =>
        label switch
        {
            "vad" => RuntimeStage.Vad,
            "diarization" => RuntimeStage.Diarization,
            "asr-whisper-small" or "asr-whisper-medium"
                or "asr-qwen-0.6b" or "asr-qwen-1.7b" => RuntimeStage.Asr,
            "translation-phi" => RuntimeStage.TextRefinement,
            "translation-madlad" => RuntimeStage.Translation,
            "tts-chatterbox" => RuntimeStage.Tts,
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown TRT RTX smoke label."),
        };

    private static string ResolveSmokeModelRootPath(BenchmarkModelCandidate candidate)
    {
        if (string.Equals(Path.GetFileName(candidate.ModelPath), "genai_config.json", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(candidate.ModelPath)
                ?? throw new InvalidOperationException($"Could not resolve GenAI model root for '{candidate.ModelPath}'.");
        }

        return candidate.RootDirectory
            ?? Path.GetDirectoryName(candidate.ModelPath)
            ?? throw new InvalidOperationException($"Could not resolve model root for '{candidate.DisplayName}'.");
    }
}
