using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

/// <summary>
/// Unlike <see cref="FakeProjectStageRunStore"/>, <see cref="UpdateAsync"/> on this store
/// honors its <see cref="CancellationToken"/> the way a real ADO.NET/SQLite-backed store
/// would: an already-canceled token throws <see cref="OperationCanceledException"/>
/// immediately. This is required to exercise <c>StageRunHelper.PersistTerminalAsync</c>'s
/// canceled-token fallback path for real, rather than trivially passing because the fake
/// ignores cancellation entirely. <see cref="CreateAsync"/> and
/// <see cref="ListByProjectAsync"/> do not check the token — the regression this fixture
/// exists for only exercises the update path.
/// </summary>
public sealed class FakeCancellationAwareProjectStageRunStore : IProjectStageRunStore
{
    private readonly List<StageRunRecord> records = [];

    public IReadOnlyList<StageRunRecord> All => records;

    public Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        records.Add(stageRun);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index = records.FindIndex(r => r.Id == stageRun.Id);
        if (index >= 0)
        {
            records[index] = stageRun;
        }
        else
        {
            records.Add(stageRun);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<StageRunRecord> result = records.Where(r => r.ProjectId == projectId).ToList();
        return Task.FromResult(result);
    }
}
