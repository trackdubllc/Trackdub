using Trackdub.Contracts.LipSynthesis;
using Trackdub.Domain.LipSynthesis;

namespace Trackdub.TestDoubles;

public sealed class FakeLipSynthesisSegmentRepository : ILipSynthesisSegmentRepository
{
    private sealed record StoredSegment(Guid ProjectId, Guid StageRunId, LipSynthesisSegment Segment);

    private readonly Dictionary<Guid, StoredSegment> store = [];

    public IReadOnlyList<LipSynthesisSegment> All => store.Values.Select(s => s.Segment).ToList();

    public Task<IReadOnlyList<LipSynthesisSegment>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LipSynthesisSegment> result = store.Values
            .Where(s => s.ProjectId == projectId)
            .Select(s => s.Segment)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<LipSynthesisSegment>> GetByStageRunAsync(
        Guid stageRunId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LipSynthesisSegment> result = store.Values
            .Where(s => s.StageRunId == stageRunId)
            .Select(s => s.Segment)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAllAsync(
        Guid projectId,
        Guid stageRunId,
        IReadOnlyList<LipSynthesisSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (LipSynthesisSegment segment in segments)
        {
            store[segment.SegmentId] = new StoredSegment(projectId, stageRunId, segment);
        }

        return Task.CompletedTask;
    }
}
