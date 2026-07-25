using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IDatabaseMigrator
{
    Task MigrateAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaVersionRecord>> GetAppliedVersionsAsync(CancellationToken cancellationToken = default);
}
