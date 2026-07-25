using Trackdub.Domain;

namespace Trackdub.Application.Runtime;

public interface IRuntimeSelectionService
{
    Task<RuntimeRoute> SelectRouteAsync(
        RuntimeStage stage,
        ExecutionProviderKind? preference = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderCapability>> GetCapabilitiesAsync(
        CancellationToken cancellationToken = default);
}
