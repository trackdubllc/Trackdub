using System.Data.Common;
using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IArtifactRepository
{
    Task RegisterAsync(
        DbConnection connection,
        ArtifactRecord artifact,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArtifactRecord>> ListByProjectAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default);
}
