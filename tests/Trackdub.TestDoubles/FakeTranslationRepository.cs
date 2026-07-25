using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Translation;

namespace Trackdub.TestDoubles;

public sealed class FakeTranslationRepository : ITranslationRepository
{
    private readonly List<TranslationRevision> revisions = [];
    private readonly Dictionary<Guid, List<TranslatedSegment>> segmentsByRevision = [];

    public IReadOnlyList<TranslationRevision> Revisions => revisions;

    /// <summary>Seed a revision and its segments without triggering revision-number logic.</summary>
    public void Seed(TranslationRevision revision, IReadOnlyList<TranslatedSegment>? segments = null)
    {
        revisions.Add(revision);
        segmentsByRevision[revision.Id] = segments is null ? [] : [.. segments];
    }

    public Task<TranslationRevision?> GetCurrentRevisionAsync(
        Guid projectId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        TranslationRevision? revision = revisions
            .Where(r => r.ProjectId == projectId &&
                        string.Equals(r.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault();
        return Task.FromResult(revision);
    }

    public Task<IReadOnlyList<TranslatedSegment>> GetSegmentsAsync(
        Guid translationRevisionId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TranslatedSegment> result = segmentsByRevision.TryGetValue(
            translationRevisionId, out List<TranslatedSegment>? segments)
            ? segments
            : [];
        return Task.FromResult(result);
    }

    public Task<int> GetNextRevisionNumberAsync(
        Guid projectId,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        int max = revisions
            .Where(r => r.ProjectId == projectId &&
                        string.Equals(r.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.RevisionNumber)
            .DefaultIfEmpty(0)
            .Max();
        return Task.FromResult(max + 1);
    }

    public Task SaveRevisionAsync(
        TranslationRevision revision,
        IReadOnlyList<TranslatedSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(segments);

        int index = revisions.FindIndex(r => r.Id == revision.Id);
        if (index >= 0)
        {
            revisions[index] = revision;
        }
        else
        {
            revisions.Add(revision);
        }

        segmentsByRevision[revision.Id] = [.. segments];
        return Task.CompletedTask;
    }
}
