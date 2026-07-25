using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Mixing;

namespace Trackdub.Application.Mixing;

public sealed class MixPlanStore(IArtifactStore artifactStore)
{
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));

    public Task SaveAsync(MixPlan mixPlan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mixPlan);
        return artifactStore.WriteJsonAsync(ProjectArtifactPaths.MixPlanRelativePath, mixPlan, cancellationToken);
    }

    public Task<MixPlan?> LoadAsync(CancellationToken cancellationToken) =>
        artifactStore.ReadJsonAsync<MixPlan>(ProjectArtifactPaths.MixPlanRelativePath, cancellationToken);
}
