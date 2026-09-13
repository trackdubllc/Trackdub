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
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            cancellationToken);

    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            new OnnxExecutionProviderSmokeTester(),
            targets,
            cancellationToken);

    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IExecutionProviderSmokeTester smokeTester,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            smokeTester,
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            cancellationToken);

    public static async Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IExecutionProviderSmokeTester smokeTester,
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(smokeTester);
        ArgumentNullException.ThrowIfNull(targets);

        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? manifestError))
        {
            throw new InvalidOperationException(manifestError ?? "Bundled model manifest was not found.");
        }

        var resolver = new BenchmarkModelPathResolver(registry, modelCacheDirectory);
        var results = new List<TrtRtxStarterPackSmokeTargetResult>(targets.Count);
        int attempted = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;

        foreach (TrtRtxSmokeCatalog.Target target in targets)
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
                stage = ResolveStage(entry);
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
            try
            {
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    ex.Message));
            }
        }

        return new TrtRtxStarterPackSmokeReport(attempted, passed, failed, skipped, results);
    }

    private static RuntimeStage ResolveStage(BundledModelManifestEntry entry)
    {
        if (entry.EngineFamily.Equals("phi-genai", StringComparison.OrdinalIgnoreCase)
            || entry.EngineFamily.Equals("qwen-instruct", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeStage.TextRefinement;
        }

        return entry.Task.Trim().ToLowerInvariant() switch
        {
            "vad" => RuntimeStage.Vad,
            "asr" => RuntimeStage.Asr,
            "translation" => RuntimeStage.Translation,
            "tts" => RuntimeStage.Tts,
            "diarization" => RuntimeStage.Diarization,
            "separation" => RuntimeStage.Separation,
            "speech-enhancement" => RuntimeStage.SpeechEnhancement,
            "forced-alignment" => RuntimeStage.LipSync,
            "text-refinement" => RuntimeStage.TextRefinement,
            "overlap-rescue" => RuntimeStage.OverlapRescue,
            "lip-synthesis" => RuntimeStage.LipSynthesis,
            "face-detection" or "face-landmarks" => RuntimeStage.LipSynthesis,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Task, "Unknown TRT RTX smoke task."),
        };
    }

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
