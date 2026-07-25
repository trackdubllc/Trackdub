using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

public sealed class FakeProjectStageRunStore : IProjectStageRunStore
{
    private readonly List<StageRunRecord> records = [];

    public IReadOnlyList<StageRunRecord> All => records;

    public int CreateCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }

    public Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stageRun);
        records.Add(stageRun);
        CreateCallCount++;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stageRun);
        int index = records.FindIndex(r => r.Id == stageRun.Id);
        if (index >= 0)
        {
            records[index] = stageRun;
        }
        else
        {
            records.Add(stageRun);
        }

        UpdateCallCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<StageRunRecord> result = records
            .Where(r => r.ProjectId == projectId)
            .ToList();
        return Task.FromResult(result);
    }
}
