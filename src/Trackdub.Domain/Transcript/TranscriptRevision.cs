namespace Trackdub.Domain.Transcript;

public sealed record TranscriptRevision(
    Guid Id,
    Guid ProjectId,
    Guid? StageRunId,
    int RevisionNumber,
    DateTimeOffset CreatedAtUtc)
{
    public static TranscriptRevision Create(
        Guid projectId,
        Guid? stageRunId,
        int revisionNumber,
        DateTimeOffset createdAtUtc)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (revisionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revisionNumber), "Revision number must be positive.");
        }

        if (stageRunId is { } runId && runId == Guid.Empty)
        {
            throw new ArgumentException("Stage run id must be null or a non-empty GUID.", nameof(stageRunId));
        }

        return new TranscriptRevision(
            Guid.NewGuid(),
            projectId,
            stageRunId,
            revisionNumber,
            createdAtUtc);
    }
}
