namespace Trackdub.Domain.Speakers;

public sealed record SpeakerTurn(
    Guid Id,
    Guid ProjectId,
    Guid SpeakerId,
    double StartSeconds,
    double EndSeconds,
    double? Confidence = null,
    bool HasOverlap = false,
    Guid? StageRunId = null)
{
    public static SpeakerTurn Create(
        Guid projectId,
        Guid speakerId,
        double startSeconds,
        double endSeconds,
        double? confidence = null,
        bool hasOverlap = false,
        Guid? stageRunId = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (speakerId == Guid.Empty)
        {
            throw new ArgumentException("Speaker id is required.", nameof(speakerId));
        }

        if (!double.IsFinite(startSeconds) || startSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Turn start must be finite and non-negative.");
        }

        if (!double.IsFinite(endSeconds) || endSeconds <= startSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "Turn end must be finite and greater than the start.");
        }

        if (confidence is double confidenceValue &&
            (!double.IsFinite(confidenceValue) || confidenceValue < 0d || confidenceValue > 1d))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0 and 1 when provided.");
        }

        return new SpeakerTurn(
            Guid.NewGuid(),
            projectId,
            speakerId,
            startSeconds,
            endSeconds,
            confidence,
            hasOverlap,
            stageRunId);
    }
}
