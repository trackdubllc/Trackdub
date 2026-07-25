using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed record OverlapRescueStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    ProjectArtifact SourceAudioArtifact,
    IReadOnlyList<OverlapRegion> Regions,
    IReadOnlyList<ProjectArtifact> ExistingArtifacts,
    string? PreferredModelAlias = null,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record OverlapRescueRegionResult(
    int RegionIndex,
    double StartSeconds,
    double EndSeconds,
    ProjectArtifact SourceCandidate0,
    ProjectArtifact SourceCandidate1,
    ProjectArtifact MetadataArtifact,
    bool PermutationWarning,
    string? SkipReason = null);

public sealed record OverlapRescueStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<OverlapRescueRegionResult> Regions,
    IReadOnlyList<ProjectArtifact> Artifacts);

public sealed class OverlapRescueStageHandler(
    IOverlapRescueEngine overlapRescueEngine,
    IAudioClipExtractor audioClipExtractor,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    IProjectStageRunStore stageRunStore,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null,
    PipelineDegradationWriter? degradationWriter = null)
{
    private readonly IOverlapRescueEngine overlapRescueEngine = overlapRescueEngine ?? throw new ArgumentNullException(nameof(overlapRescueEngine));
    private readonly IAudioClipExtractor audioClipExtractor = audioClipExtractor ?? throw new ArgumentNullException(nameof(audioClipExtractor));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<OverlapRescueStageResult> HandleAsync(
        OverlapRescueStageRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        StageRunRecord stageRun = await StageRunHelper
            .StartAsync(stageRunStore, request.ProjectId, StageNames.OverlapRescue, cancellationToken)
            .ConfigureAwait(false);

        if (request.Regions.Count == 0)
        {
            const string skipReason = "No overlap regions detected; overlap speech rescue skipped.";

            if (degradationWriter is not null)
            {
                try
                {
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.OverlapRescue,
                            "OVERLAP_RESCUE_NO_REGIONS",
                            skipReason,
                            Detail: null,
                            SelectedFallback: null,
                            RecommendedAction: null,
                            DateTimeOffset.UtcNow,
                            stageRun.Id),
                        request.ProjectId,
                        request.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Failed to write degradation record for overlap rescue no regions.", ex);
                }
            }

            stageRun = await StageRunHelper
                .SkipAsync(stageRunStore, stageRun, overlapRescueEngine as IStageRuntimeExecutionReporter,
                    skipReason, CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            return new OverlapRescueStageResult(stageRun, [], []);
        }

        string tempDirectory = OverlapRescueTempDirectories.GetRunDirectory(stageRun.Id);
        Directory.CreateDirectory(tempDirectory);
        string sourceAudioPath = artifactStore.GetPath(request.SourceAudioArtifact.RelativePath);
        var regionResults = new List<OverlapRescueRegionResult>();
        var artifacts = new List<ProjectArtifact>();
        int totalRegions = request.Regions.Count;

        try
        {
            for (int regionIndex = 0; regionIndex < totalRegions; regionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OverlapRegion region = request.Regions[regionIndex];
                string regionTempPath = Path.Combine(tempDirectory, $"region-{regionIndex}.wav");
                string candidate0TempPath = Path.Combine(tempDirectory, $"region-{regionIndex}-candidate-0.wav");
                string candidate1TempPath = Path.Combine(tempDirectory, $"region-{regionIndex}-candidate-1.wav");

                await audioClipExtractor
                    .ExtractAsync(sourceAudioPath, region.StartSeconds, region.EndSeconds, regionTempPath, cancellationToken)
                    .ConfigureAwait(false);

                OverlapRescueResult rescueResult = await overlapRescueEngine.RescueAsync(
                    new OverlapRescueRequest(
                        regionTempPath,
                        candidate0TempPath,
                        candidate1TempPath,
                        region.StartSeconds,
                        region.EndSeconds,
                        request.PreferredModelAlias,
                        request.PreferredExecutionProvider?.ToString(),
                        request.RequirePreferredExecutionProvider,
                        request.PreferredModelVariantAlias),
                    progress is null
                        ? null
                        : new Progress<OverlapRescueProgress>(update =>
                        {
                            progress.Report(update with
                            {
                                CompletedRegions = regionIndex,
                                TotalRegions = totalRegions,
                                RegionIndex = regionIndex,
                                RegionStartSeconds = region.StartSeconds,
                                RegionEndSeconds = region.EndSeconds
                            });
                        }),
                    cancellationToken).ConfigureAwait(false);

                progress?.Report(new OverlapRescueProgress(
                    CompletedRegions: regionIndex + 1,
                    TotalRegions: totalRegions,
                    RegionIndex: regionIndex,
                    RegionStartSeconds: region.StartSeconds,
                    RegionEndSeconds: region.EndSeconds,
                    IsPersistingArtifacts: true));

                string candidate0RelativePath = ProjectArtifactPaths.GetOverlapSourceCandidateRelativePath(stageRun.Id, regionIndex, 0);
                string candidate1RelativePath = ProjectArtifactPaths.GetOverlapSourceCandidateRelativePath(stageRun.Id, regionIndex, 1);
                string metadataRelativePath = ProjectArtifactPaths.GetOverlapRescueMetadataRelativePath(stageRun.Id, regionIndex);

                await CommitFileAsync(candidate0TempPath, candidate0RelativePath, cancellationToken).ConfigureAwait(false);
                await CommitFileAsync(candidate1TempPath, candidate1RelativePath, cancellationToken).ConfigureAwait(false);

                string engineFamily = ResolveEngineFamily(rescueResult);
                var metadataPayload = new OverlapRescueRegionMetadata(
                    regionIndex,
                    region.StartSeconds,
                    region.EndSeconds,
                    rescueResult.PermutationWarning,
                    region.DetectionSource,
                    engineFamily,
                    stageRun.Id);
                await CommitMetadataAsync(metadataRelativePath, metadataPayload, cancellationToken).ConfigureAwait(false);

                FileFingerprint candidate0Fingerprint = await fileFingerprintService
                    .ComputeAsync(artifactStore.GetPath(candidate0RelativePath), cancellationToken)
                    .ConfigureAwait(false);
                FileFingerprint candidate1Fingerprint = await fileFingerprintService
                    .ComputeAsync(artifactStore.GetPath(candidate1RelativePath), cancellationToken)
                    .ConfigureAwait(false);
                FileFingerprint metadataFingerprint = await fileFingerprintService
                    .ComputeAsync(artifactStore.GetPath(metadataRelativePath), cancellationToken)
                    .ConfigureAwait(false);

                string baseProvenance =
                    $"engine_family={engineFamily};region_index={regionIndex};overlap_detection={region.DetectionSource};parent_stage_run_id={stageRun.Id:D}";

                ProjectArtifact candidate0Artifact = CreateArtifact(
                    request,
                    stageRun,
                    ArtifactKind.OverlapSourceCandidate,
                    candidate0RelativePath,
                    candidate0Fingerprint,
                    rescueResult,
                    $"{baseProvenance};source_candidate=0;permutation_warning={rescueResult.PermutationWarning.ToString().ToLowerInvariant()}");
                ProjectArtifact candidate1Artifact = CreateArtifact(
                    request,
                    stageRun,
                    ArtifactKind.OverlapSourceCandidate,
                    candidate1RelativePath,
                    candidate1Fingerprint,
                    rescueResult,
                    $"{baseProvenance};source_candidate=1;permutation_warning={rescueResult.PermutationWarning.ToString().ToLowerInvariant()}");
                ProjectArtifact metadataArtifact = CreateArtifact(
                    request,
                    stageRun,
                    ArtifactKind.OverlapRescueMetadata,
                    metadataRelativePath,
                    metadataFingerprint,
                    rescueResult,
                    baseProvenance);

                await mediaAssetRepository.SaveArtifactAsync(candidate0Artifact, cancellationToken).ConfigureAwait(false);
                await mediaAssetRepository.SaveArtifactAsync(candidate1Artifact, cancellationToken).ConfigureAwait(false);
                await mediaAssetRepository.SaveArtifactAsync(metadataArtifact, cancellationToken).ConfigureAwait(false);

                regionResults.Add(new OverlapRescueRegionResult(
                    regionIndex,
                    region.StartSeconds,
                    region.EndSeconds,
                    candidate0Artifact,
                    candidate1Artifact,
                    metadataArtifact,
                    rescueResult.PermutationWarning));
                artifacts.Add(candidate0Artifact);
                artifacts.Add(candidate1Artifact);
                artifacts.Add(metadataArtifact);
            }

            bool anyPermutationWarning = regionResults.Any(static region => region.PermutationWarning);
            if (anyPermutationWarning && degradationWriter is not null)
            {
                try
                {
                    int affectedCount = regionResults.Count(r => r.PermutationWarning);
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.OverlapRescue,
                            "OVERLAP_RESCUE_PERMUTATION_WARNING",
                            "One or more overlap regions completed with source-candidate permutation warnings.",
                            Detail: $"{affectedCount} of {regionResults.Count} regions affected",
                            SelectedFallback: null,
                            RecommendedAction: "Review overlap rescue results; source candidates may be swapped.",
                            DateTimeOffset.UtcNow,
                            stageRun.Id),
                        request.ProjectId,
                        request.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Failed to write degradation record for overlap rescue permutation warning.", ex);
                }
            }

            stageRun = anyPermutationWarning
                ? await StageRunHelper
                    .PartiallyCompleteAsync(stageRunStore, stageRun, overlapRescueEngine,
                        "Overlap rescue completed with source-candidate permutation warnings.", cancellationToken, runtimePlanningPreferences)
                    .ConfigureAwait(false)
                : await StageRunHelper
                    .CompleteAsync(stageRunStore, stageRun, overlapRescueEngine, cancellationToken, runtimePlanningPreferences)
                    .ConfigureAwait(false);

            return new OverlapRescueStageResult(stageRun, regionResults, artifacts);
        }
        catch (OperationCanceledException)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, overlapRescueEngine, "Overlap rescue canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StageRunHelper
                .FailAsync(stageRunStore, stageRun, overlapRescueEngine, ex.Message, cancellationToken, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            OverlapRescueTempDirectories.DeleteIfExists(tempDirectory);
        }
    }

    private static string ResolveEngineFamily(OverlapRescueResult result)
    {
        if (result.Metadata is not null &&
            result.Metadata.TryGetValue("engine_family", out string? engineFamily) &&
            !string.IsNullOrWhiteSpace(engineFamily))
        {
            return engineFamily.Trim().ToLowerInvariant();
        }

        return "sepformer";
    }

    private async Task CommitFileAsync(string sourcePath, string relativePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length == 0)
        {
            throw new FileNotFoundException("Overlap rescue output was not created.", sourcePath);
        }

        await using ArtifactWriteHandle handle = artifactStore.CreateWriteHandle(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(handle.TemporaryPath)!);
        File.Copy(sourcePath, handle.TemporaryPath, overwrite: true);
        await artifactStore.CommitAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitMetadataAsync(
        string relativePath,
        OverlapRescueRegionMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using ArtifactWriteHandle handle = artifactStore.CreateWriteHandle(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(handle.TemporaryPath)!);
        await File.WriteAllTextAsync(
            handle.TemporaryPath,
            JsonSerializer.Serialize(metadata, OverlapRescueJson.Options),
            cancellationToken).ConfigureAwait(false);
        await artifactStore.CommitAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    private static ProjectArtifact CreateArtifact(
        OverlapRescueStageRequest request,
        StageRunRecord stageRun,
        ArtifactKind kind,
        string relativePath,
        FileFingerprint fingerprint,
        OverlapRescueResult result,
        string provenance) =>
        new(
            Guid.NewGuid(),
            request.ProjectId,
            request.MediaAsset.Id,
            kind,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            result.DurationSeconds,
            result.SampleRate,
            result.ChannelCount,
            DateTimeOffset.UtcNow,
            stageRun.Id,
            provenance);
}

internal sealed record OverlapRescueRegionMetadata(
    int RegionIndex,
    double StartSeconds,
    double EndSeconds,
    bool PermutationWarning,
    string OverlapDetection,
    string EngineFamily,
    Guid ParentStageRunId);

internal static class OverlapRescueJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

internal static class OverlapRescueTempDirectories
{
    private const string Prefix = "trackdub-overlap-rescue-";

    public static string GetRunDirectory(Guid stageRunId) =>
        Path.Combine(Path.GetTempPath(), $"{Prefix}{stageRunId:N}");

    public static void DeleteIfExists(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
