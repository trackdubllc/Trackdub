namespace Trackdub.Application.Hardware;

public interface IHardwarePolicyCoordinator
{
    Task<bool> ApplyAndEvictAsync(CancellationToken cancellationToken = default);
}
