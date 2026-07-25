using Trackdub.Domain.Tts;

namespace Trackdub.Contracts;

/// <summary>
/// Repository for managing TTS candidate groups, which enable A/B voice comparison
/// during preview by grouping multiple TTS take variants for a single segment.
/// </summary>
public interface ITtsCandidateGroupRepository
{
    /// <summary>
    /// Retrieves the candidate group for a specific translated segment.
    /// </summary>
    /// <param name="translatedSegmentId">The translated segment identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The candidate group if found, otherwise null.</returns>
    Task<TtsCandidateGroup?> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken ct);

    /// <summary>
    /// Saves or updates a candidate group.
    /// </summary>
    /// <param name="group">The candidate group to save.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveAsync(TtsCandidateGroup group, CancellationToken ct);

    /// <summary>
    /// Deletes a candidate group by its identifier.
    /// </summary>
    /// <param name="groupId">The candidate group identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(Guid groupId, CancellationToken ct);

    /// <summary>
    /// Retrieves all candidate groups for a project, ordered by segment index.
    /// </summary>
    /// <param name="projectId">The project identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TtsCandidateGroup>> GetByProjectAsync(Guid projectId, CancellationToken ct);
}
