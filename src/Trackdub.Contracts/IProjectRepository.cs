using Trackdub.Domain.Projects;

namespace Trackdub.Contracts;

public interface IProjectRepository
{
    Task InitializeAsync(TrackdubProject project, CancellationToken cancellationToken);

    Task UpdateAsync(TrackdubProject project, CancellationToken cancellationToken);

    Task<TrackdubProject?> GetAsync(CancellationToken cancellationToken);
}
