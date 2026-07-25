using Trackdub.Contracts;
using Trackdub.Domain.Tts;

namespace Trackdub.TestDoubles;

public sealed class FakeTtsCandidateGroupRepository : ITtsCandidateGroupRepository
{
    private readonly List<TtsCandidateGroup> groups = [];

    public IReadOnlyList<TtsCandidateGroup> All => groups;

    public void Seed(TtsCandidateGroup group) => groups.Add(group);

    public Task<TtsCandidateGroup?> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken ct)
    {
        TtsCandidateGroup? group = groups.FirstOrDefault(g => g.TranslatedSegmentId == translatedSegmentId);
        return Task.FromResult(group);
    }

    public Task SaveAsync(TtsCandidateGroup group, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(group);
        int index = groups.FindIndex(g => g.TranslatedSegmentId == group.TranslatedSegmentId);
        if (index >= 0)
        {
            groups[index] = group;
        }
        else
        {
            groups.Add(group);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid groupId, CancellationToken ct)
    {
        groups.RemoveAll(g => g.Id == groupId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TtsCandidateGroup>> GetByProjectAsync(Guid projectId, CancellationToken ct)
    {
        IReadOnlyList<TtsCandidateGroup> result = groups
            .Where(g => g.ProjectId == projectId)
            .OrderBy(g => g.SegmentIndex)
            .ToList();
        return Task.FromResult(result);
    }
}
