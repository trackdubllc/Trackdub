using Trackdub.Contracts.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

public interface IAtomicRevisionCommitBoundary
{
    Task<TranslationRevision> CommitRevisionAsync(
        AtomicRevisionCommitRequest request,
        Func<TranslationRevision, CancellationToken, Task<PreparedCommitArtifact>> prepareArtifact,
        CancellationToken cancellationToken);
    Task ValidateTakeInputAsync(AtomicTakeInput input, CancellationToken cancellationToken);
    Task CommitTakeAsync(AtomicTakeCommitRequest request, PreparedCommitArtifact artifact, CancellationToken cancellationToken);
}

/// <summary>The application acceptance boundary for staged translation and synthesis output.</summary>
public sealed class AtomicRevisionCommitBoundary(IAtomicRevisionPersistence persistence) : IAtomicRevisionCommitBoundary
{
    public Task<TranslationRevision> CommitRevisionAsync(
        AtomicRevisionCommitRequest request,
        Func<TranslationRevision, CancellationToken, Task<PreparedCommitArtifact>> prepareArtifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Revision.TargetLanguage);
        request = request with { Revision = request.Revision with { TargetLanguage = request.Revision.TargetLanguage.Trim().ToLowerInvariant() } };
        if (request.Segments.Any(s => s.TranslationRevisionId != request.Revision.Id)
            || request.Segments.Select(s => s.SegmentIndex).Distinct().Count() != request.Segments.Count)
        {
            throw new InvalidDataException("Revision commit contains foreign or duplicate translated segments.");
        }

        if (request.Stream is { } stream)
        {
            PipelineStreamItemFactory.ValidateTranslationStreamContext(stream.RunId, stream.SnapshotId, stream.SourceRevisionId);
            if (stream.RunId != request.Revision.StageRunId || stream.SourceRevisionId != request.Revision.SourceTranscriptRevisionId
                || stream.SnapshotId != $"{stream.SourceRevisionId:N}:{request.Revision.TargetLanguage}")
            {
                throw new InvalidDataException("Translation commit context does not match its revision.");
            }

            var expected = stream.ExpectedSegmentIndices.ToHashSet();
            if (expected.Count != stream.ExpectedSegmentIndices.Count)
                throw new InvalidDataException("Translation commit contains duplicate expected segment identities.");
            var seen = new HashSet<int>();
            int previous = -1;
            long sequence = 0;
            foreach (PipelineStreamItem<TranslatedTextSegment> item in stream.Items)
            {
                PipelineStreamIdentity identity = item.Identity;
                if (identity.RunId != stream.RunId || identity.SnapshotId != stream.SnapshotId
                    || identity.RevisionId != stream.SourceRevisionId || identity.Stage != RuntimeStage.Translation
                    || identity.Sequence != sequence++ || identity.SegmentIndex != item.Payload.Index
                    || item.Payload.Index <= previous || !expected.Contains(item.Payload.Index) || !seen.Add(item.Payload.Index))
                {
                    throw new InvalidDataException("Translation commit contains an invalid stream identity or source revision.");
                }

                previous = item.Payload.Index;
            }

            if (!seen.SetEquals(expected))
            {
                throw new InvalidDataException("Translation commit contains an incomplete stream.");
            }
        }

        return persistence.CommitRevisionAsync(request, prepareArtifact, cancellationToken);
    }

    public Task ValidateTakeInputAsync(AtomicTakeInput input, CancellationToken cancellationToken) =>
        persistence.ValidateTakeInputAsync(input, cancellationToken);

    public async Task CommitTakeAsync(AtomicTakeCommitRequest request, PreparedCommitArtifact artifact, CancellationToken cancellationToken)
    {
        await using (artifact.ConfigureAwait(false))
        {
            if (request.Take.ProjectId != request.Input.ProjectId || request.Take.TranslatedSegmentId != request.Input.Segment.Id
                || request.Take.SegmentIndex != request.Input.Segment.SegmentIndex || request.Take.ArtifactId != artifact.Artifact.Id
                || request.Take.Status != Trackdub.Domain.Tts.TtsTakeStatus.Completed || request.Take.IsStale
                || request.Take.TranslatedTextHash != TtsTextHash.Compute(request.Input.Segment.SegmentIndex, request.Input.Segment.Text))
            {
                throw new InvalidDataException("Take commit does not match its synthesis input and artifact.");
            }

            await persistence.CommitTakeAsync(request, artifact, cancellationToken).ConfigureAwait(false);
        }
    }
}
