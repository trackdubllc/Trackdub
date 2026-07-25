using Trackdub.Domain.Tts;

namespace Trackdub.Contracts.Transcripts;

public interface IVoiceAssignmentRepository
{
    Task<VoiceAssignment?> GetAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VoiceAssignment>> GetAllAsync(Guid projectId, CancellationToken cancellationToken);
    Task SaveAsync(VoiceAssignment assignment, CancellationToken cancellationToken);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
