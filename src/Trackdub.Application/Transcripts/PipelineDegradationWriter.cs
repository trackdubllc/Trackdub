using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Artifacts;

namespace Trackdub.Application.Transcripts;

public sealed class PipelineDegradationWriter(
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository)
{
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));

    public async Task WriteAsync(
        PipelineDegradationRecord record,
        Guid projectId,
        Guid mediaAssetId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        Guid artifactId = Guid.NewGuid();
        string relativePath = ProjectArtifactPaths.GetPipelineDegradationRelativePath(artifactId);

        await artifactStore.WriteJsonAsync(relativePath, record, cancellationToken).ConfigureAwait(false);

        FileFingerprint fingerprint = await fileFingerprintService.ComputeAsync(
            artifactStore.GetPath(relativePath),
            cancellationToken).ConfigureAwait(false);

        var artifact = new ProjectArtifact(
            artifactId,
            projectId,
            mediaAssetId,
            ArtifactKind.PipelineDegradation,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            DurationSeconds: null,
            SampleRate: null,
            ChannelCount: null,
            record.OccurredAtUtc,
            StageRunId: record.StageRunId,
            Provenance: record.Code,
            DegradationCode: record.Code,
            DegradationStage: record.Stage);

        await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
    }
}
