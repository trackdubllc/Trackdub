using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Tts;

namespace Trackdub.TestDoubles;

public sealed class FakeTtsTakeRepository : ITtsTakeRepository
{
    private readonly List<TtsTake> takes = [];

    public IReadOnlyList<TtsTake> All => takes;

    public void Seed(TtsTake take) => takes.Add(take);

    public Task<TtsTake?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        TtsTake? take = takes.FirstOrDefault(t => t.Id == id);
        return Task.FromResult(take);
    }

    public Task<TtsTake?> GetByFingerprintAsync(Guid projectId, string inputFingerprint, CancellationToken cancellationToken)
    {
        TtsTake? take = takes
            .Where(t => t.ProjectId == projectId &&
                        t.InputFingerprint == inputFingerprint &&
                        !t.IsStale &&
                        t.Status == TtsTakeStatus.Completed)
            .OrderByDescending(t => t.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(take);
    }

    public Task<IReadOnlyList<TtsTake>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsTake> result = takes
            .Where(t => t.ProjectId == projectId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TtsTake>> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsTake> result = takes
            .Where(t => t.TranslatedSegmentId == translatedSegmentId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TtsTake>> GetStaleBySpeakerAsync(
        Guid projectId,
        Guid voiceAssignmentId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TtsTake> result = takes
            .Where(t => t.ProjectId == projectId &&
                        t.VoiceAssignmentId == voiceAssignmentId &&
                        t.IsStale)
            .ToList();
        return Task.FromResult(result);
    }

    public Task MarkBySegmentIndicesStaleAsync(
        Guid projectId,
        IReadOnlySet<int> segmentIndices,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < takes.Count; i++)
        {
            TtsTake t = takes[i];
            if (t.ProjectId == projectId && segmentIndices.Contains(t.SegmentIndex))
            {
                takes[i] = t.MarkStale();
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkByVoiceAssignmentStaleAsync(
        Guid projectId,
        Guid voiceAssignmentId,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < takes.Count; i++)
        {
            TtsTake t = takes[i];
            if (t.ProjectId == projectId && t.VoiceAssignmentId == voiceAssignmentId)
            {
                takes[i] = t.MarkStale();
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveAsync(TtsTake take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(take);
        int index = takes.FindIndex(t => t.Id == take.Id);
        if (index >= 0)
        {
            takes[index] = take;
        }
        else
        {
            takes.Add(take);
        }

        return Task.CompletedTask;
    }
}
