using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public static class StageArtifactResumeEvaluator
{
    public static StageRunRecord? GetLatestSuccessfulRun(
        IReadOnlyList<StageRunRecord> runs,
        string stageName) =>
        runs
            .Where(r => string.Equals(r.StageName, stageName, StringComparison.OrdinalIgnoreCase))
            .Where(IsSuccessfulRun)
            .OrderByDescending(static r => r.CompletedAtUtc ?? r.StartedAtUtc)
            .FirstOrDefault();

    public static bool RuntimeMatchesSnapshot(
        StageRunRecord run,
        string stageName,
        IReadOnlyDictionary<string, string> snapshot)
    {
        if (snapshot.TryGetValue($"Model:{stageName}", out string? expectedModel) &&
            !string.IsNullOrWhiteSpace(expectedModel))
        {
            string? actualAlias = run.RuntimeInfo?.ModelAlias;
            if (string.IsNullOrWhiteSpace(actualAlias) ||
                !string.Equals(actualAlias, expectedModel, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (snapshot.TryGetValue($"ModelVariant:{stageName}", out string? expectedVariant) &&
            !string.IsNullOrWhiteSpace(expectedVariant))
        {
            string? actualVariant = run.RuntimeInfo?.ModelVariant;
            if (string.IsNullOrWhiteSpace(actualVariant) ||
                !string.Equals(actualVariant, expectedVariant, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (snapshot.TryGetValue($"ModelId:{stageName}", out string? expectedModelId) &&
            !string.IsNullOrWhiteSpace(expectedModelId))
        {
            string? actualModelId = run.RuntimeInfo?.ModelId;
            if (string.IsNullOrWhiteSpace(actualModelId) ||
                !string.Equals(actualModelId, expectedModelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static bool OutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        string stageName,
        string projectRootPath,
        string? targetLanguageCode = null,
        string? exportRelativePath = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        IReadOnlyList<ProjectArtifact> artifacts = state.ProjectState.Artifacts;

        return stageName switch
        {
            _ when string.Equals(stageName, StageNames.Vad, StringComparison.OrdinalIgnoreCase) =>
                ArtifactExists(artifactStore, TranscriptWorkflowUtilities.GetLatestArtifactByKind(artifacts, ArtifactKind.SpeechRegions)),

            _ when string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase) =>
                AsrOutputsPresent(state, artifactStore, artifacts),

            _ when string.Equals(stageName, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase) =>
                GetLatestSuccessfulRun(state.StageRuns, StageNames.TextRefinementAsr) is not null &&
                TextRefinementOutputsPresent(state, artifactStore),

            _ when string.Equals(stageName, StageNames.Diarization, StringComparison.OrdinalIgnoreCase) =>
                DiarizationOutputsPresent(state, artifactStore, artifacts),

            _ when string.Equals(stageName, StageNames.SpeakerAssignment, StringComparison.OrdinalIgnoreCase) =>
                SpeakerAssignmentOutputsPresent(state, artifactStore, artifacts),

            _ when string.Equals(stageName, StageNames.Separation, StringComparison.OrdinalIgnoreCase) =>
                ArtifactExists(artifactStore, TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(artifacts)),

            _ when string.Equals(stageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase) =>
                state.CurrentTranslationRevision is not null &&
                !state.IsTranslationStale &&
                (targetLanguageCode is null ||
                 string.Equals(state.SelectedTranslationTargetLanguage, targetLanguageCode, StringComparison.OrdinalIgnoreCase)),

            _ when string.Equals(stageName, StageNames.Tts, StringComparison.OrdinalIgnoreCase) =>
                TtsOutputsPresent(state, artifactStore, artifacts),

            _ when string.Equals(stageName, StageNames.LipSync, StringComparison.OrdinalIgnoreCase) =>
                LipSyncOutputsPresent(state.StageRuns, artifacts, artifactStore),

            _ when string.Equals(stageName, StageNames.AudioPreparation, StringComparison.OrdinalIgnoreCase) =>
                StageRunScopedArtifactsPresent(
                    state.StageRuns,
                    artifacts,
                    artifactStore,
                    StageNames.AudioPreparation,
                    ArtifactKind.AudioQualityAnalysis),

            _ when string.Equals(stageName, StageNames.SpeechEnhancement, StringComparison.OrdinalIgnoreCase) =>
                StageRunScopedArtifactsPresent(
                    state.StageRuns,
                    artifacts,
                    artifactStore,
                    StageNames.SpeechEnhancement,
                    ArtifactKind.SpeechEnhancedAudio),

            _ when string.Equals(stageName, StageNames.Export, StringComparison.OrdinalIgnoreCase) =>
                TryResolveProjectScopedPath(projectRootPath, exportRelativePath, out string? exportPath) &&
                File.Exists(exportPath),

            _ when string.Equals(stageName, StageNames.OverlapRescue, StringComparison.OrdinalIgnoreCase) =>
                OverlapRescueOutputsPresent(state.StageRuns, artifacts, artifactStore),

            _ => false
        };
    }

    public static bool CanResumeStage(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        string stageName,
        IReadOnlyDictionary<string, string> snapshot,
        string projectRootPath,
        string? targetLanguageCode = null,
        string? exportRelativePath = null)
    {
        StageRunRecord? latestRun = GetLatestSuccessfulRun(state.StageRuns, stageName);
        if (latestRun is null)
        {
            return false;
        }

        if (!RuntimeMatchesSnapshot(latestRun, stageName, snapshot))
        {
            return false;
        }

        if (!SourceLanguageMatchesSnapshot(stageName, state, snapshot))
        {
            return false;
        }

        if (!AsrUpstreamMatchesSnapshot(stageName, state, snapshot))
        {
            return false;
        }

        return OutputsPresent(
            state,
            artifactStore,
            stageName,
            projectRootPath,
            targetLanguageCode,
            exportRelativePath);
    }

    private static bool TryResolveProjectScopedPath(
        string projectRootPath,
        string? relativePath,
        out string? absolutePath)
    {
        absolutePath = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string normalizedRoot = Path.GetFullPath(projectRootPath);
        string normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            return false;
        }

        string candidatePath = Path.GetFullPath(normalizedRelative, normalizedRoot);
        string rootWithSeparator = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        absolutePath = candidatePath;
        return true;
    }

    private static bool OverlapRescueOutputsPresent(
        IReadOnlyList<StageRunRecord> runs,
        IReadOnlyList<ProjectArtifact> artifacts,
        IArtifactStore artifactStore)
    {
        StageRunRecord? latestRun = GetLatestSuccessfulRun(runs, StageNames.OverlapRescue);
        if (latestRun is null)
        {
            return false;
        }

        ProjectArtifact[] metadataArtifacts = artifacts
            .Where(a => a.StageRunId == latestRun.Id && a.Kind == ArtifactKind.OverlapRescueMetadata)
            .ToArray();

        return metadataArtifacts.Length > 0 &&
               metadataArtifacts.All(a => ArtifactExists(artifactStore, a));
    }

    private static bool LipSyncOutputsPresent(
        IReadOnlyList<StageRunRecord> runs,
        IReadOnlyList<ProjectArtifact> artifacts,
        IArtifactStore artifactStore)
    {
        StageRunRecord? latestRun = GetLatestSuccessfulRun(runs, StageNames.LipSync);
        if (latestRun is null || latestRun.Status == StageRunStatus.PartiallyCompleted)
        {
            return false;
        }

        ProjectArtifact[] lipSyncArtifacts = artifacts
            .Where(a => a.StageRunId == latestRun.Id && a.Kind == ArtifactKind.LipSyncTake)
            .ToArray();

        return lipSyncArtifacts.Length > 0 &&
               lipSyncArtifacts.All(a => ArtifactExists(artifactStore, a));
    }

    private static bool TextRefinementOutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore)
    {
        if (state.CurrentTranscriptRevision is null)
        {
            return false;
        }

        string provenanceRelativePath = ProjectArtifactPaths.GetTextRefinementProvenanceRelativePath(
            state.CurrentTranscriptRevision.Id);
        return artifactStore.Exists(provenanceRelativePath);
    }

    private static bool TtsOutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        IReadOnlyList<ProjectArtifact> artifacts)
    {
        if (state.CurrentTranslationRevision is null || state.TranslatedSegments.Count == 0)
        {
            return false;
        }

        Dictionary<Guid, ProjectArtifact> artifactsById = artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.TtsTake)
            .ToDictionary(artifact => artifact.Id);

        Dictionary<int, TtsTake> latestTakesBySegmentIndex = state.TtsTakes
            .GroupBy(take => take.SegmentIndex)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(take => take.CreatedAtUtc)
                    .First());

        foreach (TranslatedSegment segment in state.TranslatedSegments)
        {
            if (!latestTakesBySegmentIndex.TryGetValue(segment.SegmentIndex, out TtsTake? take))
            {
                return false;
            }

            if (take.Status != TtsTakeStatus.Completed || take.IsStale)
            {
                return false;
            }

            if (take.ArtifactId is not Guid artifactId ||
                !artifactsById.TryGetValue(artifactId, out ProjectArtifact? artifact))
            {
                return false;
            }

            if (!ArtifactExists(artifactStore, artifact))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StageRunScopedArtifactsPresent(
        IReadOnlyList<StageRunRecord> runs,
        IReadOnlyList<ProjectArtifact> artifacts,
        IArtifactStore artifactStore,
        string stageName,
        ArtifactKind kind)
    {
        StageRunRecord? latestRun = GetLatestSuccessfulRun(runs, stageName);
        if (latestRun is null || latestRun.Status == StageRunStatus.PartiallyCompleted)
        {
            return false;
        }

        ProjectArtifact[] stageArtifacts = artifacts
            .Where(a => a.StageRunId == latestRun.Id && a.Kind == kind)
            .ToArray();

        return stageArtifacts.Length > 0 &&
               stageArtifacts.All(a => ArtifactExists(artifactStore, a));
    }

    private static bool AsrUpstreamMatchesSnapshot(
        string stageName,
        TranscriptProjectState state,
        IReadOnlyDictionary<string, string> snapshot)
    {
        if (!string.Equals(stageName, StageNames.SpeakerAssignment, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(stageName, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(stageName, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase))
        {
            StageRunRecord? asrRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.Asr);
            return asrRun is not null && RuntimeMatchesSnapshot(asrRun, StageNames.Asr, snapshot);
        }

        if (state.CurrentTranscriptRevision?.StageRunId is not Guid revisionRunId)
        {
            return false;
        }

        StageRunRecord? refinementRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.TextRefinementAsr);
        if (refinementRun is not null && revisionRunId == refinementRun.Id)
        {
            return RuntimeMatchesSnapshot(refinementRun, StageNames.TextRefinementAsr, snapshot);
        }

        StageRunRecord? latestAsrRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.Asr);
        return latestAsrRun is not null &&
               revisionRunId == latestAsrRun.Id &&
               RuntimeMatchesSnapshot(latestAsrRun, StageNames.Asr, snapshot);
    }

    private static bool SourceLanguageMatchesSnapshot(
        string stageName,
        TranscriptProjectState state,
        IReadOnlyDictionary<string, string> snapshot)
    {
        if (!string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(stageName, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(stageName, StageNames.SpeakerAssignment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!snapshot.TryGetValue("SourceLanguage", out string? requestedLanguage) ||
            string.IsNullOrWhiteSpace(requestedLanguage))
        {
            return true;
        }

        string? normalizedRequested = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(requestedLanguage);
        if (normalizedRequested is null)
        {
            return true;
        }

        string? persistedLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(state.TranscriptLanguage);
        if (persistedLanguage is null && state.TranscriptSegments.Count > 0)
        {
            persistedLanguage = TranscriptWorkflowUtilities.ResolveDetectedTranscriptLanguage(state.TranscriptSegments);
        }

        if (persistedLanguage is null)
        {
            return true;
        }

        return string.Equals(normalizedRequested, persistedLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AsrOutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        IReadOnlyList<ProjectArtifact> artifacts)
    {
        if (state.CurrentTranscriptRevision is null || state.TranscriptSegments.Count == 0)
        {
            return false;
        }

        StageRunRecord? latestRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.Asr);
        if (latestRun is null)
        {
            return false;
        }

        ProjectArtifact? rawArtifact = artifacts
            .FirstOrDefault(artifact =>
                artifact.StageRunId == latestRun.Id &&
                artifact.Kind == ArtifactKind.TranscriptRevision &&
                string.Equals(artifact.Provenance, "generated-asr-raw", StringComparison.OrdinalIgnoreCase));

        return ArtifactExists(artifactStore, rawArtifact);
    }

    private static bool SpeakerAssignmentOutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        IReadOnlyList<ProjectArtifact> artifacts)
    {
        if (state.CurrentTranscriptRevision is null || state.TranscriptSegments.Count == 0)
        {
            return false;
        }

        StageRunRecord? latestRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.SpeakerAssignment);
        if (latestRun is null)
        {
            return false;
        }

        string transcriptPath = ProjectArtifactPaths.GetTranscriptRevisionRelativePath(
            state.CurrentTranscriptRevision.RevisionNumber);
        ProjectArtifact? persistedTranscriptArtifact = artifacts
            .FirstOrDefault(artifact =>
                artifact.Kind == ArtifactKind.TranscriptRevision &&
                string.Equals(artifact.RelativePath, transcriptPath, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(artifact.Provenance, "generated-asr", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(artifact.Provenance, "generated-asr-polished", StringComparison.OrdinalIgnoreCase)));

        return ArtifactExists(artifactStore, persistedTranscriptArtifact);
    }

    private static bool DiarizationOutputsPresent(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        IReadOnlyList<ProjectArtifact> artifacts)
    {
        if (state.SpeakerTurns.Count == 0)
        {
            return false;
        }

        StageRunRecord? latestRun = GetLatestSuccessfulRun(state.StageRuns, StageNames.Diarization);
        if (latestRun is null)
        {
            return false;
        }

        ProjectArtifact? diarizationArtifact = artifacts
            .FirstOrDefault(artifact =>
                artifact.StageRunId == latestRun.Id &&
                artifact.Kind == ArtifactKind.DiarizationResult);

        return ArtifactExists(artifactStore, diarizationArtifact);
    }

    private static bool IsSuccessfulRun(StageRunRecord run) =>
        run.Status is StageRunStatus.Completed;

    private static bool ArtifactExists(IArtifactStore artifactStore, ProjectArtifact? artifact) =>
        artifact is not null && artifactStore.Exists(artifact.RelativePath);
}
