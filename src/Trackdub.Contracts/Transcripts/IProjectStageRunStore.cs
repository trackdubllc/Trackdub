using Trackdub.Domain;

namespace Trackdub.Contracts.Transcripts;

public interface IProjectStageRunStore
{
    Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken);

    Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken);

    Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken);
}
