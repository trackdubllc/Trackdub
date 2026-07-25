using Trackdub.Contracts.LipSync;
using Trackdub.Domain.LipSync;

namespace Trackdub.TestDoubles;

public sealed class FakeLipSyncSegmentRepository : ILipSyncSegmentRepository
{
    private sealed record StoredSegment(Guid ProjectId, Guid StageRunId, LipSyncSegment Segment);

    private readonly Dictionary<Guid, StoredSegment> store = [];

    public IReadOnlyList<LipSyncSegment> All => store.Values.Select(s => s.Segment).ToList();

    public Task<IReadOnlyList<LipSyncSegment>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LipSyncSegment> result = store.Values
            .Where(s => s.ProjectId == projectId)
            .Select(s => s.Segment)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<LipSyncSegment>> GetByStageRunAsync(
        Guid stageRunId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LipSyncSegment> result = store.Values
            .Where(s => s.StageRunId == stageRunId)
            .Select(s => s.Segment)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAllAsync(
        Guid projectId,
        Guid stageRunId,
        IReadOnlyList<LipSyncSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (LipSyncSegment segment in segments)
        {
            store[segment.SegmentId] = new StoredSegment(projectId, stageRunId, segment);
        }

        return Task.CompletedTask;
    }
}
