using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SpeechAudioEnhancementStageHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenEnhancementSucceeds_CompletesAndPersistsEnhancedArtifact()
    {
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request, FakeSpeechAudioEnhancementService enhancementService)
            = CreateHandler();

        SpeechAudioEnhancementStageResult result = await handler.HandleAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal(StageNames.SpeechEnhancement, result.StageRun.StageName);
        Assert.Equal(ArtifactKind.SpeechEnhancedAudio, result.EnhancedAudioArtifact.Kind);
        Assert.Equal(1, enhancementService.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenModelUnavailable_SkipsAndReturnsSourceArtifact()
    {
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request, _) = CreateHandler(
            configureService: service => service.ThrowRequiredModelNotAvailable = true);

        SpeechAudioEnhancementStageResult result = await handler.HandleAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Skipped, result.StageRun.Status);
        Assert.Equal(request.SourceAudioArtifact.Id, result.EnhancedAudioArtifact.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenEnhancementFails_RecordsFailedStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request, _) = CreateHandler(
            stageRunStore,
            service => service.ThrowOnEnhance = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(request, TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenCanceled_RecordsCanceledStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request, _) = CreateHandler(stageRunStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(request, cancellation.Token));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
    }

    private static (
        SpeechAudioEnhancementStageHandler Handler,
        SpeechAudioEnhancementStageRequest Request,
        FakeSpeechAudioEnhancementService EnhancementService) CreateHandler(
        FakeProjectStageRunStore? stageRunStore = null,
        Action<FakeSpeechAudioEnhancementService>? configureService = null)
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "virtual-source.mp4",
            "virtual-source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var sourceAudioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.Vocals,
            ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid()),
            "vocals-hash",
            100,
            12.0d,
            48000,
            1,
            now);

        var enhancementService = new FakeSpeechAudioEnhancementService();
        configureService?.Invoke(enhancementService);

        var handler = new SpeechAudioEnhancementStageHandler(
            enhancementService,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            new FakeMediaAssetRepository(),
            stageRunStore ?? new FakeProjectStageRunStore());

        var request = new SpeechAudioEnhancementStageRequest(projectId, mediaAsset, sourceAudioArtifact, []);
        return (handler, request, enhancementService);
    }
}
