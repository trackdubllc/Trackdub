namespace Trackdub.Application.Transcripts;

internal sealed class MergeSpeakersHandler(
    ITranscriptRepository transcriptRepository)
{
    private readonly ITranscriptRepository transcriptRepository = transcriptRepository ?? throw new ArgumentNullException(nameof(transcriptRepository));

    public async Task HandleAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (sourceSpeakerId == Guid.Empty)
        {
            throw new ArgumentException("Source speaker id is required.", nameof(sourceSpeakerId));
        }

        if (targetSpeakerId == Guid.Empty)
        {
            throw new ArgumentException("Target speaker id is required.", nameof(targetSpeakerId));
        }

        if (sourceSpeakerId == targetSpeakerId)
        {
            throw new InvalidOperationException("Source and target speaker must be different.");
        }

        await transcriptRepository.ReassignAndMergeSpeakersAsync(
            projectId,
            sourceSpeakerId,
            targetSpeakerId,
            cancellationToken).ConfigureAwait(false);
    }
}
