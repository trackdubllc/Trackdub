using Trackdub.Application.Transcripts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TtsCandidateSelectionServiceTests
{
    private static TtsCandidateSelectionService CreateService(
        FakeTtsCandidateGroupRepository groupRepo,
        FakeTtsTakeRepository takeRepo,
        FakeArtifactStore artifactStore,
        FakeMediaAssetRepository mediaAssetRepo)
        => new(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

    [Fact]
    public async Task GetCandidatesAsync_returns_empty_when_no_group_exists()
    {
        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();
        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        IReadOnlyList<TtsTake> candidates = await service.GetCandidatesAsync(
            Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GetCandidatesAsync_returns_only_takes_belonging_to_the_group()
    {
        Guid projectId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();
        Guid translatedSegmentId = Guid.NewGuid();
        Guid candidateGroupId = Guid.NewGuid();

        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();

        // Seed two candidate takes belonging to the group
        TtsTake takeA = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1000, 24000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };
        TtsTake takeB = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1100, 24000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 1,
            Variant = TtsCandidateVariant.Candidate
        };
        // Take from a different group — should not appear
        TtsTake otherGroupTake = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1200, 24000, "fake")) with
        {
            CandidateGroupId = Guid.NewGuid(),
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };
        TtsTake staleGroupTake = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1300, 24000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 2,
            Variant = TtsCandidateVariant.Candidate
        };
        takeRepo.Seed(takeA);
        takeRepo.Seed(takeB);
        takeRepo.Seed(otherGroupTake);
        takeRepo.Seed(staleGroupTake.MarkStale());

        TtsCandidateGroup group = TtsCandidateGroup.Create(projectId, translatedSegmentId, 0, takeA.Id);
        // override Id to match candidateGroupId
        group = group with { Id = candidateGroupId };
        groupRepo.Seed(group);

        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        IReadOnlyList<TtsTake> candidates = await service.GetCandidatesAsync(
            translatedSegmentId, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(0, candidates[0].CandidateIndex);
        Assert.Equal(1, candidates[1].CandidateIndex);
        Assert.All(candidates, c => Assert.Equal(candidateGroupId, c.CandidateGroupId));
    }

    [Fact]
    public async Task SelectCandidateAsync_updates_selection_in_repository()
    {
        Guid projectId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();
        Guid translatedSegmentId = Guid.NewGuid();

        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();

        TtsTake takeA = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1000, 24000, "fake")) with
        {
            CandidateGroupId = Guid.NewGuid(),
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };
        TtsTake takeB = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(Guid.NewGuid(), 1100, 24000, "fake")) with
        {
            CandidateGroupId = takeA.CandidateGroupId,
            CandidateIndex = 1,
            Variant = TtsCandidateVariant.Candidate
        };
        takeRepo.Seed(takeA);
        takeRepo.Seed(takeB);

        // Seed group with takeA selected by default
        TtsCandidateGroup group = TtsCandidateGroup.Create(projectId, translatedSegmentId, 0, takeA.Id);
        groupRepo.Seed(group);

        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        // Switch to takeB
        await service.SelectCandidateAsync(translatedSegmentId, takeB.Id, CancellationToken.None);

        TtsCandidateGroup? updated = await groupRepo.GetBySegmentAsync(translatedSegmentId, CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal(takeB.Id, updated!.SelectedCandidateId);
    }

    [Fact]
    public async Task SelectCandidateAsync_throws_when_no_group_exists()
    {
        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();
        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SelectCandidateAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetSelectedCandidateRelativePathAsync_returns_null_when_no_group_exists()
    {
        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();
        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        string? result = await service.GetSelectedCandidateRelativePathAsync(
            Guid.NewGuid(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetSelectedCandidateRelativePathAsync_returns_artifact_path_for_selected_take()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();
        Guid translatedSegmentId = Guid.NewGuid();

        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepo = new FakeMediaAssetRepository();

        Guid artifactId = Guid.NewGuid();
        const string expectedRelativePath = "artifacts/tts/candidate-a.wav";

        ProjectArtifact artifact = new(
            artifactId, projectId, mediaAssetId, ArtifactKind.TtsTake,
            expectedRelativePath, "hash", 100, 1.5d, 48000, 1, DateTimeOffset.UtcNow);
        await mediaAssetRepo.SaveArtifactAsync(artifact, CancellationToken.None);

        TtsTake take = (TtsTake.Create(projectId, voiceAssignmentId,
                translatedSegmentId: translatedSegmentId, segmentIndex: 0)
            .Complete(artifactId, 48000, 48000, "fake")) with
        {
            CandidateGroupId = Guid.NewGuid(),
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };
        takeRepo.Seed(take);

        TtsCandidateGroup group = TtsCandidateGroup.Create(projectId, translatedSegmentId, 0, take.Id);
        groupRepo.Seed(group);

        TtsCandidateSelectionService service = CreateService(groupRepo, takeRepo, artifactStore, mediaAssetRepo);

        string? result = await service.GetSelectedCandidateRelativePathAsync(
            translatedSegmentId, CancellationToken.None);

        Assert.Equal(expectedRelativePath, result);
    }
}
