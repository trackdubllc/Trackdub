using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.WindowsMl;

public sealed class FixedWindowsMlEpDevicePolicyProvider(WindowsMlExecutionDevicePolicy policy)
    : IWindowsMlEpDevicePolicyProvider
{
    public Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<WindowsMlExecutionDevicePolicy>(cancellationToken)
            : Task.FromResult(policy);

    public void InvalidateCache()
    {
    }
}
