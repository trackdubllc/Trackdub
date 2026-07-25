using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;

namespace Trackdub.Contracts;

public interface IMediaAssetRepository
{
    Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken);

    Task UpdateSourcePathAsync(
        Guid mediaAssetId,
        string sourceFilePath,
        string sourceFileName,
        CancellationToken cancellationToken);

    /// <summary>Assumes a single asset per project. Use per-asset overload added in M28 instead.</summary>
    [Obsolete("Single-asset assumption. Replaced by GetAllAsync in M28.")]
    Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken);

    Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken);

    Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken);
}
