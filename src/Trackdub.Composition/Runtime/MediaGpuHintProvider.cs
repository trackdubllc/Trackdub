using Trackdub.Contracts;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime;

public sealed class MediaGpuHintProvider(IHardwareProfileProvider hardwareProfileProvider) : IMediaGpuHintProvider
{
    public async Task<MediaGpuHint> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        HardwareProfile profile = await hardwareProfileProvider
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        return MediaGpuHints.FromProfile(profile.HasGpu, profile.GpuDescription);
    }
}
