using Trackdub.Application.Hardware;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Hardware;

public sealed class HardwarePolicyCoordinator(
    IWindowsMlEpDevicePolicyProvider devicePolicyProvider,
    IInferenceSessionPoolEvictor sessionPoolEvictor,
    IApplicationLogger? logger) : IHardwarePolicyCoordinator
{
    public async Task<bool> ApplyAndEvictAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            devicePolicyProvider.InvalidateCache();
            await sessionPoolEvictor.EvictAllIdleAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Failed to evict idle ONNX sessions after hardware policy change.", ex);
            return false;
        }
    }
}
