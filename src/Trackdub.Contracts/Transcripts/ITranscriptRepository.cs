using Trackdub.Domain.Transcript;
using DomainTranscriptSegment = Trackdub.Domain.Transcript.TranscriptSegment;

namespace Trackdub.Contracts.Transcripts;

public interface ITranscriptRepository
{
    Task<TranscriptRevision?> GetCurrentRevisionAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DomainTranscriptSegment>> GetSegmentsAsync(Guid transcriptRevisionId, CancellationToken cancellationToken);

    Task<int> GetNextRevisionNumberAsync(Guid projectId, CancellationToken cancellationToken);

    Task SaveRevisionAsync(
        TranscriptRevision revision,
        IReadOnlyList<DomainTranscriptSegment> segments,
        CancellationToken cancellationToken);

    Task ReassignSpeakerAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken);

    Task ReassignAndMergeSpeakersAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken);
}
