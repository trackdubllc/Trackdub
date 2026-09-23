using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks;

public sealed record BenchmarkComparisonResult(
    IReadOnlyList<BenchmarkEvidenceReport> Accepted,
    IReadOnlyDictionary<Guid, string> Rejected,
    double? MedianMilliseconds,
    double? MinimumMilliseconds,
    double? MaximumMilliseconds);

/// <summary>Aggregates only successful samples with matching controlled conditions.</summary>
public static class BenchmarkComparison
{
    private static readonly HashSet<string> ModelStages = new(StringComparer.OrdinalIgnoreCase)
    {
        StageNames.Separation, StageNames.Vad, StageNames.Diarization, StageNames.Asr,
        StageNames.OverlapRescue, StageNames.TextRefinementAsr, StageNames.Translation,
        StageNames.Tts, StageNames.LipSync, StageNames.LipSynthesis,
    };

    internal static bool ProviderMatches(string requested, string actual) =>
        string.Equals(NormalizeProvider(requested), NormalizeProvider(actual), StringComparison.Ordinal);

    private static string NormalizeProvider(string provider) =>
        new(provider.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static BenchmarkComparisonResult Compare(
        IReadOnlyList<BenchmarkEvidenceReport> samples, string metric = "pipeline")
    {
        ArgumentNullException.ThrowIfNull(samples);
        var accepted = new List<BenchmarkEvidenceReport>();
        var rejected = new Dictionary<Guid, string>();
        BenchmarkEvidenceReport? reference = null;
        foreach (BenchmarkEvidenceReport sample in samples)
        {
            string? reason = Validate(sample, metric);
            if (reason is null && reference is not null && !Compatible(reference, sample))
                reason = "Fixture, scenario, mode, model, provider, configuration, or runtime differs.";
            if (reason is null)
            {
                reference ??= sample;
                accepted.Add(sample);
            }
            else
            {
                rejected[sample.RunId] = reason;
            }
        }
        double[] values = accepted.Select(x => x.TimingsMilliseconds[metric]!.Value).Order().ToArray();
        double? median = values.Length == 0 ? null :
            values.Length % 2 == 1 ? values[values.Length / 2] :
            (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        return new BenchmarkComparisonResult(
            accepted, rejected, median,
            values.Length == 0 ? null : values[0],
            values.Length == 0 ? null : values[^1]);
    }

    private static string? Validate(BenchmarkEvidenceReport sample, string metric)
    {
        if (sample.Kind != BenchmarkEvidenceKind.Benchmark) return "Observation is not a controlled benchmark.";
        if (sample.Status != BenchmarkEvidenceStatus.Completed) return "Sample did not complete successfully.";
        if (sample.FixtureSha256 is not { Length: 64 }) return "Fixture checksum unavailable.";
        if (!sample.TimingsMilliseconds.TryGetValue(metric, out double? value) ||
            value is null or <= 0 || !double.IsFinite(value.Value))
            return "Requested measurement unavailable.";
        if (sample.RequestedProvider is not null &&
            (sample.ActualProvider is null ||
             !ProviderMatches(sample.RequestedProvider, sample.ActualProvider)))
            return "Requested provider did not execute.";
        if (ModelStages.Contains(sample.Scenario) &&
            sample.ActualProvider is null)
            return "Actual provider unavailable.";
        if (sample.Scenario == "full-pipeline" &&
            sample.Stages.Any(stage =>
                ModelStages.Contains(stage.Name) && stage.ActualProvider is null))
            return "A stage's actual provider is unavailable.";
        if (sample.Stages.Any(x => x.Status != BenchmarkEvidenceStatus.Completed))
            return "Stage skipped, failed, or partially completed.";
        return null;
    }

    private static bool Compatible(BenchmarkEvidenceReport left, BenchmarkEvidenceReport right) =>
        left.FixtureSha256 == right.FixtureSha256 &&
        left.Scenario == right.Scenario &&
        left.RunMode == right.RunMode &&
        left.RequestedModel == right.RequestedModel &&
        left.ActualModel == right.ActualModel &&
        left.RequestedProvider == right.RequestedProvider &&
        left.ActualProvider == right.ActualProvider &&
        EqualMaps(left.Configuration, right.Configuration) &&
        EqualMaps(left.RuntimeVersions, right.RuntimeVersions);

    private static bool EqualMaps(
        IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out string? value) && pair.Value == value);
}
