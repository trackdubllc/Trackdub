using Trackdub.Contracts;

namespace Trackdub.Application.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        string currentVersion,
        CancellationToken cancellationToken = default);
}
