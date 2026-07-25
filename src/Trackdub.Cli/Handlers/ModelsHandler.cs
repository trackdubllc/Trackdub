using System.Text.Json;

using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Extended model inventory operations for the CLI.
/// </summary>
internal static class ModelsHandler
{
    public static async Task<IReadOnlyList<ModelInventoryEntry>> GetInventoryAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        IModelInventoryService inventory = factory.GetRequiredService<IModelInventoryService>();
        return await inventory.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ModelDownloadResult> DownloadModelAsync(
        TrackdubSessionFactory factory,
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        IModelDownloadOrchestrator orchestrator = factory.GetRequiredService<IModelDownloadOrchestrator>();
        try
        {
            return await orchestrator
                .DownloadAsync(modelId, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ModelDownloadResult(
                modelId,
                Success: false,
                NewState: ModelCacheState.Missing,
                FailureReason: "Download cancelled.",
                Cancelled: true);
        }
    }

    public static async Task<int> VerifyAsync(
        TrackdubSessionFactory factory,
        string? modelId,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        IModelCacheVerifier verifier = factory.GetRequiredService<IModelCacheVerifier>();

        if (modelId is not null)
        {
            ModelVerificationResult result = await verifier
                .VerifyAsync(modelId, cancellationToken)
                .ConfigureAwait(false);

            var payload = new ModelVerifyRow
            {
                ModelId = result.ModelId,
                PreviousState = result.PreviousState,
                NewState = result.NewState,
                HashMatch = result.HashMatch,
                FailureReason = result.FailureReason,
            };

            string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
            await output.WriteLineAsync(json).ConfigureAwait(false);
            return result.HashMatch ? Program.ExitSuccess : Program.ExitPipelineFailure;
        }

        IReadOnlyList<ModelVerificationResult> results = await verifier
            .VerifyAllAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var rows = results
            .Select(result => new ModelVerifyRow
            {
                ModelId = result.ModelId,
                PreviousState = result.PreviousState,
                NewState = result.NewState,
                HashMatch = result.HashMatch,
                FailureReason = result.FailureReason,
            })
            .ToList();

        await output.WriteLineAsync(JsonSerializer.Serialize(rows, CliJsonOptions.Default)).ConfigureAwait(false);
        return rows.All(row => row.HashMatch) ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    public static async Task<int> BundleNeededAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var checker = new TrackdubPipelineReadinessChecker(factory);
        PipelineReadinessReport report = await checker
            .EvaluateDefaultPipelineAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        IModelInventoryService inventory = factory.GetRequiredService<IModelInventoryService>();
        IReadOnlyList<ModelInventoryEntry> entries = await inventory
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        var needed = report.Stages
            .Where(stage => stage.Status.IsBlocking())
            .Select(stage => new BundleNeededRow
            {
                Stage = stage.StageName,
                ReadinessState = stage.Status,
                ModelId = stage.ModelId,
                ModelAlias = stage.ModelAlias,
                Detail = stage.Detail,
                DownloadCommand = stage.ModelId is not null
                    ? $"trackdub models download {stage.ModelId}"
                    : null,
                InventoryState = entries.FirstOrDefault(entry =>
                        string.Equals(entry.ModelId, stage.ModelId, StringComparison.OrdinalIgnoreCase))
                    ?.State,
            })
            .ToList();

        var payload = new BundleNeededOutput
        {
            Ready = report.IsRunReady,
            Models = needed,
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return report.IsRunReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    public static IReadOnlyList<ModelInventoryEntry> FilterStatusEntries(
        IReadOnlyList<ModelInventoryEntry> entries,
        string? filter,
        bool missingOnly)
    {
        IEnumerable<ModelInventoryEntry> query = entries;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (filter.StartsWith("stage:", StringComparison.OrdinalIgnoreCase))
            {
                string stageName = filter["stage:".Length..].Trim();
                string taskName = MapStageToTask(stageName);
                query = query.Where(entry =>
                    string.Equals(entry.Task, taskName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                query = query.Where(entry =>
                    entry.ModelId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || entry.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || (entry.Aliases?.Any(alias =>
                        alias.Contains(filter, StringComparison.OrdinalIgnoreCase)) ?? false));
            }
        }

        if (missingOnly)
        {
            query = query.Where(entry =>
                entry.State is not ModelCacheState.Ready and not ModelCacheState.Installed);
        }

        return query.ToList();
    }

    public static async Task<int> DownloadAllMissingAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        IModelInventoryService inventory = factory.GetRequiredService<IModelInventoryService>();
        IReadOnlyList<ModelInventoryEntry> entries = await inventory
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        IModelDownloadOrchestrator orchestrator = factory.GetRequiredService<IModelDownloadOrchestrator>();
        var missing = entries
            .Where(entry => entry.CommercialAllowed)
            .Where(entry => entry.CanAutoDownload)
            .Where(entry => entry.State is not ModelCacheState.Ready and not ModelCacheState.Installed)
            .ToList();

        if (missing.Count == 0)
        {
            Console.WriteLine("No missing commercial bundled models to download.");
            return Program.ExitSuccess;
        }

        bool allSucceeded = true;
        foreach (ModelInventoryEntry entry in missing)
        {
            ModelDownloadResult result = await orchestrator
                .DownloadAsync(entry.ModelId, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result.Cancelled)
            {
                Console.WriteLine($"Download cancelled for {entry.ModelId}.");
                return Program.ExitCancelled;
            }

            if (!result.Success)
            {
                allSucceeded = false;
                CliErrorReporter.ReportError(
                    ErrorCode.ModelNotAvailable,
                    result.FailureReason ?? $"Download failed for '{entry.ModelId}'.");
            }
            else
            {
                Console.WriteLine($"Downloaded {entry.ModelId} ({result.NewState}).");
            }
        }

        return allSucceeded ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    private static string MapStageToTask(string stageName) =>
        stageName.ToLowerInvariant() switch
        {
            "separation" => "separation",
            "vad" => "vad",
            "diarization" => "diarization",
            "asr" => "asr",
            "translation" => "translation",
            "tts" => "tts",
            _ => stageName,
        };

    private sealed class ModelVerifyRow
    {
        public string? ModelId { get; init; }
        public ModelCacheState PreviousState { get; init; }
        public ModelCacheState NewState { get; init; }
        public bool HashMatch { get; init; }
        public string? FailureReason { get; init; }
    }

    private sealed class BundleNeededOutput
    {
        public bool Ready { get; init; }
        public List<BundleNeededRow>? Models { get; init; }
    }

    private sealed class BundleNeededRow
    {
        public string? Stage { get; init; }
        public ReadinessState ReadinessState { get; init; }
        public string? ModelId { get; init; }
        public string? ModelAlias { get; init; }
        public string? Detail { get; init; }
        public ModelCacheState? InventoryState { get; init; }
        public string? DownloadCommand { get; init; }
    }
}
