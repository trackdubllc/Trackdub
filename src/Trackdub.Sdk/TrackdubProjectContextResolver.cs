using Trackdub.Application.Dubbing;

namespace Trackdub.Sdk;

/// <summary>
/// Opens an existing project and reads persisted media/language settings from SQLite.
/// </summary>
public static class TrackdubProjectContextResolver
{
    /// <summary>
    /// Opens the project at <paramref name="projectRootPath"/> when a database is present.
    /// </summary>
    public static async Task<TrackdubProjectContext?> TryOpenAsync(
        TrackdubSessionFactory factory,
        string projectRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        DubbingProjectContext? context = await DubbingProjectContextResolver
            .TryOpenAsync(factory, projectRootPath, cancellationToken)
            .ConfigureAwait(false);
        return context is null ? null : TrackdubProjectContext.From(context);
    }
}
