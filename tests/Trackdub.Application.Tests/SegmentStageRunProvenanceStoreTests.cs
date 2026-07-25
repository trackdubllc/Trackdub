using Trackdub.Application.Projects;

namespace Trackdub.Application.Tests;

public sealed class SegmentStageRunProvenanceStoreTests
{
    [Fact]
    public void RecordTranslationRuns_updates_only_selected_indices_and_inherits_others()
    {
        Guid existingRun = Guid.NewGuid();
        Guid newRun = Guid.NewGuid();
        ProjectUiSettings settings = new(
            SegmentStageRuns: new SegmentStageRunMap(
                Translation: new Dictionary<int, Guid>
                {
                    [0] = existingRun,
                    [1] = existingRun,
                }));

        ProjectUiSettings updated = SegmentStageRunProvenanceStore.RecordTranslationRuns(
            settings,
            [0, 1, 2],
            new HashSet<int> { 2 },
            newRun,
            revisionStageRunIdForInheritance: existingRun);

        Assert.Equal(existingRun, updated.SegmentStageRuns!.Translation![0]);
        Assert.Equal(existingRun, updated.SegmentStageRuns.Translation[1]);
        Assert.Equal(newRun, updated.SegmentStageRuns.Translation[2]);
    }

    [Fact]
    public void RecordAsrRuns_seeds_all_segment_indices()
    {
        Guid asrRun = Guid.NewGuid();
        ProjectUiSettings updated = SegmentStageRunProvenanceStore.RecordAsrRuns(
            settings: null,
            [0, 1, 2],
            new HashSet<int> { 0, 1, 2 },
            asrRun);

        Assert.Equal(asrRun, updated.SegmentStageRuns!.Asr![0]);
        Assert.Equal(asrRun, updated.SegmentStageRuns.Asr[1]);
        Assert.Equal(asrRun, updated.SegmentStageRuns.Asr[2]);
    }

    [Fact]
    public void ResolveStageRunId_prefers_segment_map_over_revision_fallback()
    {
        Guid mapped = Guid.NewGuid();
        Guid revision = Guid.NewGuid();
        SegmentStageRunMap map = new(Translation: new Dictionary<int, Guid> { [4] = mapped });

        Guid? resolved = SegmentStageRunProvenanceStore.ResolveStageRunId(4, map, translation: true, revision);

        Assert.Equal(mapped, resolved);
    }
}
