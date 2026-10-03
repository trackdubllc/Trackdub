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

        return File.Exists(Path.Join(projectRootPath, ProjectArtifactPaths.DatabaseFileName));
    }

    /// <summary>
    /// Resolves the project directory a run writes into: <paramref name="projectOutputDirectory"/>
    /// when the caller supplied one, otherwise a <c>.trackdub</c> folder beside the source media.
    /// </summary>
    /// <remarks>
    /// Single source of truth for the derivation. Hosts that act on a project directory before
    /// the run starts — notably acquiring <c>ProjectLock</c> — must resolve it through this method,
    /// or they guard a different directory than the engine writes to.
    /// </remarks>
    public static string ResolveProjectDirectory(string sourceMediaPath, string? projectOutputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceMediaPath);

        return projectOutputDirectory
            ?? Path.Join(
                Path.GetDirectoryName(sourceMediaPath) ?? ".",
                Path.GetFileNameWithoutExtension(sourceMediaPath) + ".trackdub");
    }
}
