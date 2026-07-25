using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class GenerateCandidatesHandlerTests
{
    private static GenerateCandidatesHandler CreateHandler(
        FakeTtsEngine? ttsEngine = null,
        FakeVoiceCatalog? voiceCatalog = null,
        FakeArtifactStore? artifactStore = null,
        FakeFileFingerprintService? fingerprintService = null,
        FakeMediaAssetRepository? mediaAssetRepo = null,
        FakeTtsTakeRepository? takeRepo = null,
        FakeTtsCandidateGroupRepository? groupRepo = null)
        => new(
            ttsEngine ?? new FakeTtsEngine(),
            voiceCatalog ?? new FakeVoiceCatalog(),
            artifactStore ?? new FakeArtifactStore(),
            fingerprintService ?? new FakeFileFingerprintService(),
            mediaAssetRepo ?? new FakeMediaAssetRepository(),
            takeRepo ?? new FakeTtsTakeRepository(),
            groupRepo ?? new FakeTtsCandidateGroupRepository());

    private static GenerateCandidatesRequest BuildRequest(
        int candidateCount = 2,
        Guid? translatedSegmentId = null) =>
        new(
            ProjectId: Guid.NewGuid(),
            VoiceAssignmentId: Guid.NewGuid(),
            SpeakerId: Guid.NewGuid(),
            MediaAssetId: Guid.NewGuid(),
            TranslatedSegmentId: translatedSegmentId ?? Guid.NewGuid(),
            SegmentIndex: 0,
            SegmentText: "Hello world",
            TargetLanguage: "es",
            CandidateCount: candidateCount);

    [Fact]
    public async Task HandleAsync_generates_requested_number_of_candidates()
    {
        var ttsEngine = new FakeTtsEngine();
        var artifactStore = new FakeArtifactStore();
        GenerateCandidatesHandler handler = CreateHandler(ttsEngine: ttsEngine, artifactStore: artifactStore);

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 3), CancellationToken.None);

        Assert.Equal(3, result.Candidates.Count);
        Assert.Equal(3, ttsEngine.SynthesizeCallCount);
    }

    [Fact]
    public async Task HandleAsync_passes_default_inference_options_to_tts_engine()
    {
        // CommercialSafeMode is no longer a runtime flag on InferenceRequestOptions;
        // commercial safety is enforced at manifest authoring time. The handler is
        // expected to pass InferenceRequestOptions.Default (no special overrides).
        var ttsEngine = new FakeTtsEngine();
        GenerateCandidatesHandler handler = CreateHandler(ttsEngine: ttsEngine);

        _ = await handler.HandleAsync(BuildRequest(candidateCount: 1), CancellationToken.None);

        Assert.NotNull(ttsEngine.LastOptions);
        Assert.Equal(InferenceRequestOptions.Default, ttsEngine.LastOptions);
    }

    [Fact]
    public async Task HandleAsync_candidates_have_sequential_indices()
    {
        GenerateCandidatesHandler handler = CreateHandler();

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 3), CancellationToken.None);

        Assert.Equal(0, result.Candidates[0].CandidateIndex);
        Assert.Equal(1, result.Candidates[1].CandidateIndex);
        Assert.Equal(2, result.Candidates[2].CandidateIndex);
    }

    [Fact]
    public async Task HandleAsync_candidates_share_the_same_group_id()
    {
        GenerateCandidatesHandler handler = CreateHandler();

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 2), CancellationToken.None);

        Guid? groupId = result.Candidates[0].CandidateGroupId;
        Assert.NotNull(groupId);
        Assert.All(result.Candidates, c => Assert.Equal(groupId, c.CandidateGroupId));
    }

    [Fact]
    public async Task HandleAsync_creates_candidate_group_pointing_to_first_take_by_default()
    {
        GenerateCandidatesHandler handler = CreateHandler();

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 2), CancellationToken.None);

        Assert.Equal(result.Candidates[0].Id, result.Group.SelectedCandidateId);
    }

    [Fact]
    public async Task HandleAsync_variant_is_Candidate_on_generated_takes()
    {
        GenerateCandidatesHandler handler = CreateHandler();

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 2), CancellationToken.None);

        Assert.All(result.Candidates, c => Assert.Equal(TtsCandidateVariant.Candidate, c.Variant));
    }

    [Fact]
    public async Task HandleAsync_clamps_count_to_minimum_of_one()
    {
        var ttsEngine = new FakeTtsEngine();
        GenerateCandidatesHandler handler = CreateHandler(ttsEngine: ttsEngine);

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 0), CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Equal(1, ttsEngine.SynthesizeCallCount);
    }

    [Fact]
    public async Task HandleAsync_clamps_count_to_maximum_of_five()
    {
        var ttsEngine = new FakeTtsEngine();
        GenerateCandidatesHandler handler = CreateHandler(ttsEngine: ttsEngine);

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 99), CancellationToken.None);

        Assert.Equal(5, result.Candidates.Count);
        Assert.Equal(5, ttsEngine.SynthesizeCallCount);
    }

    [Fact]
    public async Task HandleAsync_preserves_existing_selection_when_selected_take_still_in_new_batch()
    {
        // First call — creates group + selects take[0]
        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var ttsEngine = new FakeTtsEngine();
        Guid translatedSegmentId = Guid.NewGuid();
        GenerateCandidatesRequest request = BuildRequest(candidateCount: 2, translatedSegmentId: translatedSegmentId);

        GenerateCandidatesHandler handler = CreateHandler(
            ttsEngine: ttsEngine, groupRepo: groupRepo, takeRepo: takeRepo);

        GenerateCandidatesResult first = await handler.HandleAsync(request, CancellationToken.None);

        // Manually switch selection to take[1]
        TtsCandidateGroup updatedGroup = first.Group with { SelectedCandidateId = first.Candidates[1].Id };
        await groupRepo.SaveAsync(updatedGroup, CancellationToken.None);

        // Second call — regenerate with same translated segment id
        GenerateCandidatesResult second = await handler.HandleAsync(request, CancellationToken.None);

        // The prior selection (take[1].Id) is no longer in the new batch, so it falls back to new take[0]
        Assert.Equal(second.Candidates[0].Id, second.Group.SelectedCandidateId);
    }

    [Fact]
    public async Task HandleAsync_regeneration_marks_old_candidates_stale_and_uses_new_artifact_paths()
    {
        var groupRepo = new FakeTtsCandidateGroupRepository();
        var takeRepo = new FakeTtsTakeRepository();
        var mediaAssetRepo = new FakeMediaAssetRepository();
        Guid translatedSegmentId = Guid.NewGuid();
        GenerateCandidatesRequest request = BuildRequest(candidateCount: 2, translatedSegmentId: translatedSegmentId);
        GenerateCandidatesHandler handler = CreateHandler(
            groupRepo: groupRepo,
            takeRepo: takeRepo,
            mediaAssetRepo: mediaAssetRepo);

        GenerateCandidatesResult first = await handler.HandleAsync(request, CancellationToken.None);
        GenerateCandidatesResult second = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Equal(first.Group.Id, second.Group.Id);
        Assert.All(first.Candidates, oldTake =>
        {
            TtsTake stored = Assert.Single(takeRepo.All, take => take.Id == oldTake.Id);
            Assert.True(stored.IsStale);
            Assert.Equal(TtsTakeStatus.Stale, stored.Status);
        });
        Assert.All(second.Candidates, newTake =>
        {
            Assert.False(newTake.IsStale);
            Assert.Equal(TtsTakeStatus.Completed, newTake.Status);
        });
        Assert.Equal(4, mediaAssetRepo.Artifacts.Select(static artifact => artifact.RelativePath).Distinct().Count());
    }

    [Fact]
    public async Task HandleAsync_saves_artifacts_and_takes_to_repositories()
    {
        var mediaAssetRepo = new FakeMediaAssetRepository();
        var takeRepo = new FakeTtsTakeRepository();
        GenerateCandidatesHandler handler = CreateHandler(
            mediaAssetRepo: mediaAssetRepo, takeRepo: takeRepo);

        GenerateCandidatesResult result = await handler.HandleAsync(
            BuildRequest(candidateCount: 2), CancellationToken.None);

        Assert.Equal(2, takeRepo.All.Count);
        Assert.All(result.Candidates, c => Assert.Contains(takeRepo.All, t => t.Id == c.Id));
    }
}
