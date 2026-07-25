using Trackdub.Application.Dubbing;

namespace Trackdub.Sdk;

/// <summary>
/// Helpers for locating Trackdub project artifacts on disk.
/// Forwards to <see cref="DubbingProjectPaths"/>.
/// </summary>
public static class TrackdubProjectPaths
{
    /// <summary>
    /// Returns true when <paramref name="projectRootPath"/> contains a Trackdub SQLite project database.
    /// </summary>
    public static bool ContainsDatabase(string projectRootPath) =>
        DubbingProjectPaths.ContainsDatabase(projectRootPath);
}
