using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.Domain.Artifacts;

namespace Trackdub.Contracts.Transcripts;

public sealed record TranslationStreamCommit(
    Guid RunId,
    string SnapshotId,
    Guid SourceRevisionId,
    IReadOnlyList<int> ExpectedSegmentIndices,
    IReadOnlyList<PipelineStreamItem<TranslatedTextSegment>> Items);

/// <summary>Revision.SourceTranscriptRevisionId is the expected current source head;
/// the target language is normalized before publication. Null previous means no translation head.</summary>
public sealed record AtomicRevisionCommitRequest(
    TranslationRevision Revision,
    Guid? ExpectedPreviousTranslationRevisionId,
    IReadOnlyList<TranslatedSegment> Segments,
    TranslationStreamCommit? Stream = null);

public sealed record AtomicTakeInput(
    Guid ProjectId,
    string TargetLanguage,
    Guid ExpectedSourceRevisionId,
    Guid ExpectedTranslationRevisionId,
    TranslatedSegment Segment);

public sealed record AtomicTakeCommitRequest(AtomicTakeInput Input, TtsTake Take);

/// <summary>
/// An attempt-owned temporary file. Only a successful create-only promotion establishes
/// ownership of the destination. Shared/reused files never receive such a receipt.
/// </summary>
public sealed class ArtifactPromotionReceipt(Guid attemptId, ArtifactWriteHandle handle, string expectedSha256)
{
    public Guid AttemptId { get; } = attemptId;
    public string TemporaryPath { get; } = handle.TemporaryPath;
    public string FinalPath { get; } = handle.FinalPath;
    public string ExpectedSha256 { get; } = expectedSha256;
    public bool CreatedByAttempt { get; private set; }

    public void MarkCreated() => CreatedByAttempt = true;
}

public sealed class PreparedCommitArtifact(ProjectArtifact artifact, ArtifactWriteHandle handle) : IAsyncDisposable
{
    public ProjectArtifact Artifact { get; } = artifact;
    public ArtifactWriteHandle Handle { get; } = handle;
    public ArtifactPromotionReceipt Receipt { get; } = new(Guid.NewGuid(), handle, artifact.Sha256);
    public ValueTask DisposeAsync() => Handle.DisposeAsync();
}

/// <summary>
/// Each operation uses a dedicated non-deferred write transaction: validate expected heads,
/// conditionally publish all rows, promote the owned artifact, then commit. A head conflict
/// must never be retried using newer inputs. Files are published only by committed DB references.
/// </summary>
public interface IAtomicRevisionPersistence
{
    Task<TranslationRevision> CommitRevisionAsync(
        AtomicRevisionCommitRequest request,
        Func<TranslationRevision, CancellationToken, Task<PreparedCommitArtifact>> prepareArtifact,
        CancellationToken cancellationToken);

    Task ValidateTakeInputAsync(AtomicTakeInput input, CancellationToken cancellationToken);

    Task CommitTakeAsync(
        AtomicTakeCommitRequest request,
        PreparedCommitArtifact artifact,
        CancellationToken cancellationToken);
}
