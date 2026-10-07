using Trackdub.Domain.Translation;

namespace Trackdub.Application.Tests;

/// <summary>Unit-test persistence only. Atomicity/concurrency evidence lives in the real SQLite integration suite.</summary>
internal static class TestAtomicCommitBoundary
{
    public static IAtomicRevisionCommitBoundary Create(ITranslationRepository? translations, ITtsTakeRepository takes,
        IArtifactStore store, IMediaAssetRepository media) =>
        new AtomicRevisionCommitBoundary(new Persistence(translations, takes, store, media));

    private sealed class Persistence(ITranslationRepository? translations, ITtsTakeRepository takes, IArtifactStore store,
        IMediaAssetRepository media) : IAtomicRevisionPersistence
    {
        public Task ValidateTakeInputAsync(AtomicTakeInput input, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<TranslationRevision> CommitRevisionAsync(AtomicRevisionCommitRequest request,
            Func<TranslationRevision, CancellationToken, Task<PreparedCommitArtifact>> prepareArtifact, CancellationToken cancellationToken)
        {
            ITranslationRepository repository = translations!;
            var previous = await repository.GetCurrentRevisionAsync(request.Revision.ProjectId, request.Revision.TargetLanguage, cancellationToken);
            var old = previous is null ? [] : await repository.GetSegmentsAsync(previous.Id, cancellationToken);
            var revision = request.Revision with { RevisionNumber = await repository.GetNextRevisionNumberAsync(request.Revision.ProjectId, request.Revision.TargetLanguage, cancellationToken) };
            await using var prepared = await prepareArtifact(revision, cancellationToken);
            await store.CommitAsync(prepared.Handle, cancellationToken);
            await repository.SaveRevisionAsync(revision, request.Segments, cancellationToken);
            await media.SaveArtifactAsync(prepared.Artifact, cancellationToken);
            var changed = request.Segments.Where(s => !old.Any(o => o.SegmentIndex == s.SegmentIndex && o.Text == s.Text))
                .Select(s => s.SegmentIndex).ToHashSet();
            if (changed.Count > 0) await takes.MarkBySegmentIndicesStaleAsync(revision.ProjectId, changed, cancellationToken);
            return revision;
        }

        public async Task CommitTakeAsync(AtomicTakeCommitRequest request, PreparedCommitArtifact artifact, CancellationToken cancellationToken)
        {
            await using (artifact)
            {
                await store.CommitAsync(artifact.Handle, cancellationToken);
                await media.SaveArtifactAsync(artifact.Artifact, cancellationToken);
                await takes.SaveAsync(request.Take, cancellationToken);
            }
        }
    }
}
