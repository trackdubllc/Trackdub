using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Tui;

internal static class TuiProjectHelper
{
    internal static async Task<IReadOnlyList<RecentProjectEntry>> LoadRecentProjectsAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        IStudioSettingsService settingsService = factory.GetRequiredService<IStudioSettingsService>();
        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);

        return settings.RecentProjects
            .Where(entry => Directory.Exists(entry.ProjectPath)
                && TrackdubProjectPaths.ContainsDatabase(entry.ProjectPath))
            .ToList();
    }

    internal static async Task SetOpenProjectAsync(
        TrackdubTuiContext context,
        string projectPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        string resolvedPath = Path.GetFullPath(projectPath);
        context.ProjectPath = resolvedPath;

        IStudioSettingsService settingsService = context.Factory.GetRequiredService<IStudioSettingsService>();
        await settingsService
            .TouchRecentProjectAsync(resolvedPath, projectName, cancellationToken)
            .ConfigureAwait(false);
    }
}
