namespace Trackdub.Contracts;

public interface IExplicitFfmpegInstaller
{
    Task<bool> InstallFfmpegAsync(CancellationToken ct = default);
}
