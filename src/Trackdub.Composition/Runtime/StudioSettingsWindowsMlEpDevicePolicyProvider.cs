using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Runtime;

public sealed class StudioSettingsWindowsMlEpDevicePolicyProvider(IStudioSettingsService settingsService)
    : IWindowsMlEpDevicePolicyProvider
{
    private readonly IStudioSettingsService settingsService =
        settingsService ?? throw new ArgumentNullException(nameof(settingsService));

    private readonly object cacheLock = new();
    private bool cacheLoaded;
    private int cacheVersion;
    private WindowsMlExecutionDevicePolicy cachedPolicy = WindowsMlExecutionDevicePolicy.Explicit;

    public async Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsMlExecutionDevicePolicy.Explicit;
        }

        int capturedVersion;
        lock (cacheLock)
        {
            if (cacheLoaded)
            {
                return cachedPolicy;
            }

            capturedVersion = cacheVersion;
        }

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy policy = settings.WindowsMlExecutionDevicePolicy;

        lock (cacheLock)
        {
            if (cacheVersion != capturedVersion)
            {
                return cacheLoaded ? cachedPolicy : WindowsMlExecutionDevicePolicy.Explicit;
            }

            cachedPolicy = policy;
            cacheLoaded = true;
            return cachedPolicy;
        }
    }

    public void InvalidateCache()
    {
        lock (cacheLock)
        {
            cacheLoaded = false;
            cacheVersion++;
        }
    }
}
