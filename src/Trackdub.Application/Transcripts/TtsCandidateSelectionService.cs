using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Service for managing TTS candidate selection during preview.
/// Enables A/B comparison by retrieving, selecting, and querying candidate takes.
/// </summary>
public sealed class TtsCandidateSelectionService(
    ITtsCandidateGroupRepository candidateGroupRepository,
    ITtsTakeRepository ttsTakeRepository,
    IArtifactStore artifactStore,
    IMediaAssetRepository mediaAssetRepository)
{
    private readonly ITtsCandidateGroupRepository candidateGroupRepository = candidateGroupRepository
                                                                             ?? throw new ArgumentNullException(nameof(candidateGroupRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository
                                                            ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly IArtifactStore artifactStore = artifactStore
                                                    ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository
                                                                   ?? throw new ArgumentNullException(nameof(mediaAssetRepository));

    /// <summary>
    /// Retrieves all candidate takes for a specific translated segment.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All candidate takes for the segment, ordered by candidate index.</returns>
    public async Task<IReadOnlyList<TtsTake>> GetCandidatesAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        if (group is null)
            return [];

        IReadOnlyList<TtsTake> allTakes = await ttsTakeRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        return allTakes
            .Where(take => IsActiveCandidate(take, group.Id))
            .OrderBy(take => take.CandidateIndex!.Value)
            .ToList();
    }

    /// <summary>
    /// Retrieves the currently selected candidate take for a segment.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The selected candidate take if found, otherwise null.</returns>
    public async Task<TtsTake?> GetSelectedCandidateAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        if (group is null)
            return null;

        return await ttsTakeRepository.GetAsync(group.SelectedCandidateId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Selects a specific candidate as the active choice for a segment.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="candidateId">The candidate take identifier to select.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SelectCandidateAsync(
        Guid translatedSegmentId,
        Guid candidateId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        if (group is null)
            throw new InvalidOperationException("No candidate group exists for this segment.");

        TtsCandidateGroup updated = group.SelectCandidate(candidateId);
        await candidateGroupRepository.SaveAsync(updated, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the relative artifact path for the currently selected candidate's audio artifact.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The relative artifact path if found, otherwise null.</returns>
    public async Task<string?> GetSelectedCandidateRelativePathAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsTake? selected = await GetSelectedCandidateAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        if (selected?.ArtifactId is not Guid artifactId)
            return null;

        ProjectArtifact? artifact = await mediaAssetRepository
            .GetArtifactByIdAsync(artifactId, ct)
            .ConfigureAwait(false);

        return artifact?.RelativePath;
    }

    /// <summary>
    /// Gets the absolute file path for the currently selected candidate's audio artifact.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The absolute file path if found, otherwise null.</returns>
    public async Task<string?> GetSelectedCandidatePathAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        string? relativePath = await GetSelectedCandidateRelativePathAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        return relativePath is null ? null : artifactStore.GetPath(relativePath);
    }

    /// <summary>
    /// Checks whether a segment has multiple candidates available.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the segment has multiple candidates, otherwise false.</returns>
    public async Task<bool> HasMultipleCandidatesAsync(
        Guid translatedSegmentId,
        CancellationToken ct)
    {
        TtsCandidateGroup? group = await candidateGroupRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        if (group is null)
            return false;

        IReadOnlyList<TtsTake> allTakes = await ttsTakeRepository
            .GetBySegmentAsync(translatedSegmentId, ct)
            .ConfigureAwait(false);

        return allTakes.Count(take => IsActiveCandidate(take, group.Id)) > 1;
    }

    private static bool IsActiveCandidate(TtsTake take, Guid groupId) =>
        take.CandidateGroupId == groupId &&
        take.CandidateIndex.HasValue &&
        take.Variant == TtsCandidateVariant.Candidate &&
        take.Status == TtsTakeStatus.Completed &&
        !take.IsStale;
}
