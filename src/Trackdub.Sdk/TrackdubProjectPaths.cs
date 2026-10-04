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

    /// <summary>
    /// Resolves the project directory a run writes into. Use this before acting on a project
    /// directory (for example acquiring <see cref="ProjectLock"/>) so hosts and the engine agree.
    /// </summary>
    public static string ResolveProjectDirectory(string sourceMediaPath, string? projectOutputDirectory) =>
        DubbingProjectPaths.ResolveProjectDirectory(sourceMediaPath, projectOutputDirectory);
}
