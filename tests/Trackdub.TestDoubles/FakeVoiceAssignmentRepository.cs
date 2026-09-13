using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Tts;

namespace Trackdub.TestDoubles;

public sealed class FakeVoiceAssignmentRepository : IVoiceAssignmentRepository
{
    private readonly List<VoiceAssignment> assignments = [];

    public IReadOnlyList<VoiceAssignment> All => assignments;

    public void Seed(VoiceAssignment assignment) => assignments.Add(assignment);

    public Task<VoiceAssignment?> GetAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken)
    {
        VoiceAssignment? assignment = assignments.FirstOrDefault(
            a => a.ProjectId == projectId && a.SpeakerId == speakerId && !a.IsFallback);
        return Task.FromResult(assignment);
    }

    public Task<IReadOnlyList<VoiceAssignment>> GetAllAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<VoiceAssignment> result = assignments
            .Where(a => a.ProjectId == projectId && !a.IsFallback)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(VoiceAssignment assignment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        Predicate<VoiceAssignment> matchesProjectSpeaker = candidate =>
            candidate.ProjectId == assignment.ProjectId &&
            candidate.SpeakerId == assignment.SpeakerId &&
            candidate.IsFallback == assignment.IsFallback;
        int index = assignments.FindIndex(matchesProjectSpeaker);
        if (index >= 0)
        {
            VoiceAssignment existing = assignments[index];
            assignments[index] = assignment with
            {
                Id = existing.Id,
                CreatedAtUtc = existing.CreatedAtUtc
            };
            return Task.CompletedTask;
        }

        assignments.Add(assignment);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        assignments.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }
}
