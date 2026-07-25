using Trackdub.Domain.LipSync;

namespace Trackdub.Contracts.LipSync;

public interface ILipSyncSegmentRepository
{
    Task<IReadOnlyList<LipSyncSegment>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LipSyncSegment>> GetByStageRunAsync(Guid stageRunId, CancellationToken cancellationToken);
    Task SaveAllAsync(Guid projectId, Guid stageRunId, IReadOnlyList<LipSyncSegment> segments, CancellationToken cancellationToken);
}
