using Trackdub.Contracts.Projects;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Helpers for locating Trackdub project artifacts on disk.
/// </summary>
public static class DubbingProjectPaths
{
    /// <summary>
    /// Returns true when <paramref name="projectRootPath"/> contains a Trackdub SQLite project database.
    /// </summary>
    public static bool ContainsDatabase(string projectRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        if (!Directory.Exists(projectRootPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(projectRootPath, ProjectArtifactPaths.DatabaseFileName));
    }
}
