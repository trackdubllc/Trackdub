using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;

namespace Trackdub.TestDoubles;

public sealed class FakeMediaAssetRepository : IMediaAssetRepository
{
    private readonly List<MediaAsset> assets = [];
    private readonly List<ProjectArtifact> artifacts = [];

    public IReadOnlyList<MediaAsset> Assets => assets;
    public IReadOnlyList<ProjectArtifact> Artifacts => artifacts;

    /// <summary>Seed a media asset directly without going through <see cref="SaveAsync"/>.</summary>
    public void Seed(MediaAsset asset) => assets.Add(asset);

    public Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        int index = assets.FindIndex(a => a.Id == asset.Id);
        if (index >= 0)
        {
            assets[index] = asset;
        }
        else
        {
            assets.Add(asset);
        }

        return Task.CompletedTask;
    }

    public Task UpdateSourcePathAsync(
        Guid mediaAssetId,
        string sourceFilePath,
        string sourceFileName,
        CancellationToken cancellationToken)
    {
        int index = assets.FindIndex(a => a.Id == mediaAssetId);
        if (index >= 0)
        {
            assets[index] = assets[index] with
            {
                SourceFilePath = sourceFilePath,
                SourceFileName = sourceFileName
            };
        }

        return Task.CompletedTask;
    }

    [Obsolete("Single-asset assumption. Replaced by GetAllAsync in M28.")]
    public Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken)
    {
        MediaAsset? asset = assets.FirstOrDefault(a => a.ProjectId == projectId);
        return Task.FromResult(asset);
    }

    public Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        int index = artifacts.FindIndex(a => a.Id == artifact.Id);
        if (index >= 0)
        {
            artifacts[index] = artifact;
        }
        else
        {
            artifacts.Add(artifact);
        }

        return Task.CompletedTask;
    }

    public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        artifacts.RemoveAll(a => a.Id == artifactId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectArtifact> result = artifacts
            .Where(a => a.ProjectId == projectId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaAsset> result = assets
            .Where(a => a.ProjectId == projectId)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        ProjectArtifact? artifact = artifacts.FirstOrDefault(a => a.Id == artifactId);
        return Task.FromResult(artifact);
    }
}
