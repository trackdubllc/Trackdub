using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Projects;

namespace Trackdub.Application.Projects;

/// <summary>
/// Persists per-segment stage-run ids in project UI settings so mixed model/provider runs stay attributable.
/// </summary>
public static class SegmentStageRunProvenanceStore
{
    public static ProjectUiSettings RecordTranslationRuns(
        ProjectUiSettings? settings,
        IEnumerable<int> allSegmentIndices,
        IReadOnlySet<int> updatedSegmentIndices,
        Guid stageRunId,
        Guid? revisionStageRunIdForInheritance = null)
    {
        ArgumentNullException.ThrowIfNull(allSegmentIndices);
        ArgumentNullException.ThrowIfNull(updatedSegmentIndices);
        if (stageRunId == Guid.Empty)
        {
            throw new ArgumentException("Stage run id is required.", nameof(stageRunId));
        }

        ProjectUiSettings normalized = settings?.Normalize() ?? new ProjectUiSettings();
        SegmentStageRunMap map = normalized.SegmentStageRuns ?? new SegmentStageRunMap();
        Dictionary<int, Guid> translation = map.Translation is { } existingTranslation
            ? new Dictionary<int, Guid>(existingTranslation)
            : new Dictionary<int, Guid>();

        foreach (int index in allSegmentIndices)
        {
            if (updatedSegmentIndices.Contains(index))
            {
                translation[index] = stageRunId;
            }
            else if (!translation.ContainsKey(index) && revisionStageRunIdForInheritance is Guid inherit && inherit != Guid.Empty)
            {
                translation[index] = inherit;
            }
        }

        return normalized with
        {
            SegmentStageRuns = map with { Translation = translation }
        };
    }

    public static ProjectUiSettings RecordAsrRuns(
        ProjectUiSettings? settings,
        IEnumerable<int> allSegmentIndices,
        IReadOnlySet<int> updatedSegmentIndices,
        Guid stageRunId,
        Guid? revisionStageRunIdForInheritance = null)
    {
        ArgumentNullException.ThrowIfNull(allSegmentIndices);
        ArgumentNullException.ThrowIfNull(updatedSegmentIndices);
        if (stageRunId == Guid.Empty)
        {
            throw new ArgumentException("Stage run id is required.", nameof(stageRunId));
        }

        ProjectUiSettings normalized = settings?.Normalize() ?? new ProjectUiSettings();
        SegmentStageRunMap map = normalized.SegmentStageRuns ?? new SegmentStageRunMap();
        Dictionary<int, Guid> asr = map.Asr is { } existingAsr
            ? new Dictionary<int, Guid>(existingAsr)
            : new Dictionary<int, Guid>();

        foreach (int index in allSegmentIndices)
        {
            if (updatedSegmentIndices.Contains(index))
            {
                asr[index] = stageRunId;
            }
            else if (!asr.ContainsKey(index) && revisionStageRunIdForInheritance is Guid inherit && inherit != Guid.Empty)
            {
                asr[index] = inherit;
            }
        }

        return normalized with
        {
            SegmentStageRuns = map with { Asr = asr }
        };
    }

    public static Guid? ResolveStageRunId(
        int segmentIndex,
        SegmentStageRunMap? map,
        bool translation,
        Guid? revisionStageRunId)
    {
        IReadOnlyDictionary<int, Guid>? segmentMap = translation ? map?.Translation : map?.Asr;
        if (segmentMap is not null && segmentMap.TryGetValue(segmentIndex, out Guid mapped) && mapped != Guid.Empty)
        {
            return mapped;
        }

        return revisionStageRunId is Guid revisionId && revisionId != Guid.Empty
            ? revisionId
            : null;
    }

    public static async Task PersistUiSettingsAsync(
        IArtifactStore artifactStore,
        TrackdubProject project,
        string? transcriptLanguage,
        ProjectUiSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);

        ProjectManifest? manifest = await artifactStore
            .ReadJsonAsync<ProjectManifest>(ProjectArtifactPaths.ManifestRelativePath, cancellationToken)
            .ConfigureAwait(false);
        manifest ??= ProjectManifest.FromProject(project, transcriptLanguage);
        await artifactStore
            .WriteJsonAsync(
                ProjectArtifactPaths.ManifestRelativePath,
                manifest.WithUiSettings(settings.Normalize()),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
