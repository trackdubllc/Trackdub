namespace Trackdub.Domain.Tts;

/// <summary>
/// Groups multiple TTS take candidates for a single translated segment,
/// enabling A/B comparison and selection during preview.
/// </summary>
public sealed record TtsCandidateGroup(
    Guid Id,
    Guid ProjectId,
    Guid TranslatedSegmentId,
    int SegmentIndex,
    Guid SelectedCandidateId,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// Creates a new candidate group with the specified candidate selected by default.
    /// </summary>
    public static TtsCandidateGroup Create(
        Guid projectId,
        Guid translatedSegmentId,
        int segmentIndex,
        Guid selectedCandidateId)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (translatedSegmentId == Guid.Empty)
        {
            throw new ArgumentException("Translated segment id is required.", nameof(translatedSegmentId));
        }

        if (selectedCandidateId == Guid.Empty)
        {
            throw new ArgumentException("Selected candidate id is required.", nameof(selectedCandidateId));
        }

        if (segmentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentIndex), "Segment index cannot be negative.");
        }

        return new TtsCandidateGroup(
            Guid.NewGuid(),
            projectId,
            translatedSegmentId,
            segmentIndex,
            selectedCandidateId,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Returns a new candidate group with the specified candidate selected.
    /// </summary>
    public TtsCandidateGroup SelectCandidate(Guid candidateId)
    {
        if (candidateId == Guid.Empty)
        {
            throw new ArgumentException("Candidate id is required.", nameof(candidateId));
        }

        return this with { SelectedCandidateId = candidateId };
    }
}