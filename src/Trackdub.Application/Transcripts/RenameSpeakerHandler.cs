namespace Trackdub.Application.Transcripts;

internal sealed class RenameSpeakerHandler(ISpeakerRepository speakerRepository)
{
    private readonly ISpeakerRepository speakerRepository = speakerRepository ?? throw new ArgumentNullException(nameof(speakerRepository));

    public Task HandleAsync(
        Guid projectId,
        Guid speakerId,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (speakerId == Guid.Empty)
        {
            throw new ArgumentException("Speaker id is required.", nameof(speakerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName, nameof(displayName));

        return speakerRepository.RenameSpeakerAsync(projectId, speakerId, displayName, cancellationToken);
    }
}
