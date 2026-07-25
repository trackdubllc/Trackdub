using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Composition.HardwareProfiler;

public sealed class JsonHardwareProfilerStore(IAppStoragePaths storagePaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private string ProfilerDirectory => Path.Combine(storagePaths.UserDataRoot, "hardware-profiler");

    private string LatestSnapshotPath => Path.Combine(ProfilerDirectory, "latest.json");

    public async Task<HardwareProfilerSnapshot?> LoadLatestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(LatestSnapshotPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = File.OpenRead(LatestSnapshotPath);
            StoredHardwareProfilerSnapshot? stored = await JsonSerializer
                .DeserializeAsync<StoredHardwareProfilerSnapshot>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return stored?.ToDomain();
        }
        catch (JsonException)
        {
            TryArchiveCorruptSnapshot();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void TryArchiveCorruptSnapshot()
    {
        try
        {
            if (!File.Exists(LatestSnapshotPath))
            {
                return;
            }

            string archivePath = $"{LatestSnapshotPath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(LatestSnapshotPath, archivePath, overwrite: true);
        }
        catch (IOException)
        {
            // Best-effort recovery only.
        }
    }

    public async Task SaveLatestAsync(HardwareProfilerSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(ProfilerDirectory);
        string runDirectory = Path.Combine(ProfilerDirectory, "runs", snapshot.EvidenceId.ToString("N"));
        Directory.CreateDirectory(runDirectory);

        StoredHardwareProfilerSnapshot stored = StoredHardwareProfilerSnapshot.FromDomain(snapshot);
        string tempPath = $"{LatestSnapshotPath}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, LatestSnapshotPath, overwrite: true);

        string runCopyPath = Path.Combine(runDirectory, "snapshot.json");
        await using FileStream runStream = File.Create(runCopyPath);
        await JsonSerializer.SerializeAsync(runStream, stored, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private sealed record StoredHardwareProfilerSnapshot(
        Guid EvidenceId,
        StoredHardwareFingerprint Fingerprint,
        IReadOnlyList<StoredStageBenchmarkScenarioResult> Scenarios,
        StoredHardwarePresetRecommendation Recommendation,
        DateTimeOffset CompletedAtUtc)
    {
        public static StoredHardwareProfilerSnapshot FromDomain(HardwareProfilerSnapshot snapshot) =>
            new(
                snapshot.EvidenceId,
                StoredHardwareFingerprint.FromDomain(snapshot.Fingerprint),
                snapshot.Scenarios.Select(StoredStageBenchmarkScenarioResult.FromDomain).ToArray(),
                StoredHardwarePresetRecommendation.FromDomain(snapshot.Recommendation),
                snapshot.CompletedAtUtc);

        public HardwareProfilerSnapshot ToDomain() =>
            new(
                EvidenceId,
                Fingerprint.ToDomain(),
                Scenarios.Select(static s => s.ToDomain()).ToArray(),
                Recommendation.ToDomain(),
                CompletedAtUtc);
    }

    private sealed record StoredHardwareFingerprint(
        string Hash,
        string OperatingSystem,
        string Architecture,
        string? GpuDescription,
        long? TotalRamBytes,
        long? GpuDedicatedMemoryBytes,
        DateTimeOffset CapturedAtUtc)
    {
        public static StoredHardwareFingerprint FromDomain(HardwareFingerprint fingerprint) =>
            new(
                fingerprint.Hash,
                fingerprint.OperatingSystem,
                fingerprint.Architecture,
                fingerprint.GpuDescription,
                fingerprint.TotalRamBytes,
                fingerprint.GpuDedicatedMemoryBytes,
                fingerprint.CapturedAtUtc);

        public HardwareFingerprint ToDomain() =>
            new(
                Hash,
                OperatingSystem,
                Architecture,
                GpuDescription,
                TotalRamBytes,
                GpuDedicatedMemoryBytes,
                CapturedAtUtc);
    }

    private sealed record StoredStageBenchmarkScenarioResult(
        StageBenchmarkScenario Scenario,
        BenchmarkStatus Status,
        string RequestedProvider,
        string SelectedProvider,
        double? WarmLatencyAverageMilliseconds,
        double? RealTimeFactorAverage,
        string? FailureReason)
    {
        public static StoredStageBenchmarkScenarioResult FromDomain(StageBenchmarkScenarioResult result) =>
            new(
                result.Scenario,
                result.Status,
                result.RequestedProvider,
                result.SelectedProvider,
                result.WarmLatencyAverageMilliseconds,
                result.RealTimeFactorAverage,
                result.FailureReason);

        public StageBenchmarkScenarioResult ToDomain() =>
            new(
                Scenario,
                Status,
                RequestedProvider,
                SelectedProvider,
                WarmLatencyAverageMilliseconds,
                RealTimeFactorAverage,
                FailureReason);
    }

    private sealed record StoredHardwarePresetRecommendation(
        HardwareQualityPreset Preset,
        string Summary,
        IReadOnlyList<string> RationaleLines,
        string SuggestedModelTierPreference)
    {
        public static StoredHardwarePresetRecommendation FromDomain(HardwarePresetRecommendation recommendation) =>
            new(
                recommendation.Preset,
                recommendation.Summary,
                recommendation.RationaleLines,
                recommendation.SuggestedModelTierPreference);

        public HardwarePresetRecommendation ToDomain() =>
            new(Preset, Summary, RationaleLines, SuggestedModelTierPreference);
    }
}
