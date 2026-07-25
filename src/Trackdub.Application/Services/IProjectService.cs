namespace Trackdub.Application.Services;

using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

/// <summary>
/// Manages projects and media probing.
/// </summary>
public interface IProjectService
{
    /// <summary>Load or create a project from media file.</summary>
    Task<Project> LoadFromMediaAsync(string mediaPath);

    /// <summary>Probe media file for metadata (duration, codec, resolution).</summary>
    Task<MediaProbe> ProbeMediaAsync(string mediaPath);

    /// <summary>Save project state.</summary>
    Task SaveProjectAsync(Project project);
}
