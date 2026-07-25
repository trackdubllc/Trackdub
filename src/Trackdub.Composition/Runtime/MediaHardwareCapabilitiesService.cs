using Trackdub.Contracts;

namespace Trackdub.Composition.Runtime;

public sealed class MediaHardwareCapabilitiesService(
    IFfmpegVideoEncoderCapabilities ffmpegCapabilities,
    IMediaGpuHintProvider gpuHintProvider) : IMediaHardwareCapabilitiesService
{
    public async Task<MediaHardwareCapabilities> RefreshAsync(CancellationToken cancellationToken = default)
    {
        FfmpegVideoEncoderSnapshot ffmpegEncoders = await ffmpegCapabilities
            .RefreshAsync(cancellationToken)
            .ConfigureAwait(false);
        MediaGpuHint gpuHint = await gpuHintProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return new MediaHardwareCapabilities(ffmpegEncoders, gpuHint);
    }
}
