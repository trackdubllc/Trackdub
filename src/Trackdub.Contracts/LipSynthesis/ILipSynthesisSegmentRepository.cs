using Trackdub.Domain.LipSynthesis;

namespace Trackdub.Contracts.LipSynthesis;

public interface ILipSynthesisSegmentRepository
{
    Task<IReadOnlyList<LipSynthesisSegment>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<LipSynthesisSegment>> GetByStageRunAsync(Guid stageRunId, CancellationToken cancellationToken);
    Task SaveAllAsync(Guid projectId, Guid stageRunId, IReadOnlyList<LipSynthesisSegment> segments, CancellationToken cancellationToken);
}
