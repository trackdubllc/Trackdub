using System.Data.Common;
using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IStageRunRepository
{
    Task CreateAsync(
        DbConnection connection,
        StageRunRecord stageRun,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<StageRunRecord?> GetAsync(
        DbConnection connection,
        Guid stageRunId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        DbConnection connection,
        StageRunRecord stageRun,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);
}
