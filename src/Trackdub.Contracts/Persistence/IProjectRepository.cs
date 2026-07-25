using System.Data.Common;
using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IProjectRepository
{
    Task CreateAsync(
        DbConnection connection,
        ProjectRecord project,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default);
}
