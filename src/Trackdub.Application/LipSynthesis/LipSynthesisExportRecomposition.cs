using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.LipSynthesis;

internal static class LipSynthesisExportRecomposition
{
    public static ResolvedVideoRecompositionPlan? TryBuildResolvedPlan(
        TranscriptProjectState state,
        IArtifactStore artifactStore,
        string sourceVideoPath)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVideoPath);

        VideoRecompositionPlan? plan = TryBuildPlan(state, artifactStore);
        if (plan is null || plan.PatchedTurns.Count == 0)
        {
            return null;
        }

        var resolvedTurns = new List<ResolvedRecomposedTurn>(plan.PatchedTurns.Count);
        foreach (RecomposedTurn turn in plan.PatchedTurns)
        {
            string absoluteClipPath = artifactStore.GetPath(turn.PatchedClipRelativePath);
            if (!File.Exists(absoluteClipPath))
            {
                return null;
            }

            resolvedTurns.Add(new ResolvedRecomposedTurn(
                turn.Start,
                turn.End,
                absoluteClipPath));
        }

        return new ResolvedVideoRecompositionPlan(sourceVideoPath, resolvedTurns);
    }

    public static VideoRecompositionPlan? TryBuildPlan(
        TranscriptProjectState state,
        IArtifactStore artifactStore)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(artifactStore);

        StageRunRecord? latestRun = state.StageRuns
            .Where(run => string.Equals(run.StageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase))
            .Where(run => run.Status is StageRunStatus.Completed or StageRunStatus.PartiallyCompleted)
            .OrderByDescending(run => run.CompletedAtUtc ?? run.StartedAtUtc)
            .FirstOrDefault();

        IReadOnlyList<ProjectArtifact> lipTakes = state.ProjectState.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.LipSynthesisTake)
            .Where(artifact => latestRun is null || artifact.StageRunId == latestRun.Id)
            .Where(artifact => artifactStore.Exists(artifact.RelativePath))
            .ToArray();

        if (lipTakes.Count == 0)
        {
            return null;
        }

        Dictionary<Guid, SpeakerTurn> turnsById = state.SpeakerTurns
            .ToDictionary(turn => turn.Id);

        var patchedTurns = new List<RecomposedTurn>();
        foreach (ProjectArtifact artifact in lipTakes)
        {
            if (!TryParseTurnSegmentId(artifact.Provenance, out Guid segmentId) ||
                !turnsById.TryGetValue(segmentId, out SpeakerTurn? speakerTurn))
            {
                continue;
            }

            patchedTurns.Add(new RecomposedTurn(
                segmentId,
                TimeSpan.FromSeconds(speakerTurn.StartSeconds),
                TimeSpan.FromSeconds(speakerTurn.EndSeconds),
                artifact.RelativePath));
        }

        if (patchedTurns.Count == 0)
        {
            return null;
        }

        patchedTurns.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return new VideoRecompositionPlan(
            state.ProjectState.MediaAsset?.SourceFileName ?? "source.mp4",
            patchedTurns);
    }

    public static string? BuildExportCompositingWarning(
        TranscriptProjectState state,
        IArtifactStore artifactStore)
    {
        bool hasLipSynthesisArtifacts = state.ProjectState.Artifacts
            .Any(artifact => artifact.Kind == ArtifactKind.LipSynthesisTake);
        if (!hasLipSynthesisArtifacts)
        {
            return null;
        }

        if (TryBuildPlan(state, artifactStore) is { PatchedTurns.Count: > 0 })
        {
            return null;
        }

        return "Lip-synthesis repaired clips could not be composited into export video; output uses the original source footage.";
    }

    private static bool TryParseTurnSegmentId(string? provenance, out Guid segmentId)
    {
        segmentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(provenance))
        {
            return false;
        }

        const string prefix = "lipsynthesis:turn:";
        if (!provenance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParse(provenance[prefix.Length..], out segmentId) && segmentId != Guid.Empty;
    }
}
