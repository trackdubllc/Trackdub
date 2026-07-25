using Trackdub.Domain.Media;

namespace Trackdub.Contracts;

public interface IMediaProbe
{
    Task<MediaProbeSnapshot> ProbeAsync(string sourcePath, CancellationToken cancellationToken);
}
