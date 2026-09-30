using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SpeechAudioEnhancementOptionsPlumbingTests
{
    [Fact]
    public void FromStudioSettings_MapsAfxFields_AndClampsIntensity()
    {
        StudioSettings settings = StudioSettings.Default with
        {
            EnableNvidiaAfx = true,
            NvidiaAfxProfile = NvidiaAfxProfile.AcousticEchoCancellation,
            NvidiaAfxIntensityRatio = 2.5f
        };

        SpeechAudioEnhancementOptions options = SpeechAudioEnhancementOptions.FromStudioSettings(settings);

        Assert.True(options.EnableNvidiaAfx);
        Assert.Equal(NvidiaAfxProfile.AcousticEchoCancellation, options.NvidiaAfxProfile);
        Assert.Equal(1.0f, options.NvidiaAfxIntensityRatio);
        Assert.Null(options.FarEndReferenceAudioPath);
    }

    [Fact]
    public async Task StageHandler_PassesStudioSettingsOptions_ToEnhancementService()
    {
        var settingsService = new FixedStudioSettingsService(
            StudioSettings.Default with
            {
                EnableNvidiaAfx = true,
                NvidiaAfxProfile = NvidiaAfxProfile.NoiseOnly,
                NvidiaAfxIntensityRatio = 0.4f
            });
        var enhancementService = new FakeSpeechAudioEnhancementService();
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request) =
            CreateHandler(enhancementService, settingsService);

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(enhancementService.LastRequest);
        Assert.NotNull(enhancementService.LastRequest!.Options);
        Assert.True(enhancementService.LastRequest.Options!.EnableNvidiaAfx);
        Assert.Equal(NvidiaAfxProfile.NoiseOnly, enhancementService.LastRequest.Options.NvidiaAfxProfile);
        Assert.Equal(0.4f, enhancementService.LastRequest.Options.NvidiaAfxIntensityRatio);
    }

    [Fact]
    public async Task StageHandler_AppliesFarEndPath_FromStageRequest()
    {
        var enhancementService = new FakeSpeechAudioEnhancementService();
        (SpeechAudioEnhancementStageHandler handler, SpeechAudioEnhancementStageRequest request) =
            CreateHandler(
                enhancementService,
                settingsService: null,
                farEndPath: @"C:\refs\farend.wav");

        await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\refs\farend.wav", enhancementService.LastRequest!.Options!.FarEndReferenceAudioPath);
    }

    private static (
        SpeechAudioEnhancementStageHandler Handler,
        SpeechAudioEnhancementStageRequest Request) CreateHandler(
        FakeSpeechAudioEnhancementService enhancementService,
        IStudioSettingsService? settingsService,
        string? farEndPath = null)
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

        var handler = new SpeechAudioEnhancementStageHandler(
            enhancementService,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            new FakeMediaAssetRepository(),
            new FakeProjectStageRunStore(),
            settingsService);

        var request = new SpeechAudioEnhancementStageRequest(
            projectId,
            mediaAsset,
            sourceAudioArtifact,
            [],
            Options: null,
            FarEndReferenceAudioPath: farEndPath);
        return (handler, request);
    }

    private sealed class FixedStudioSettingsService(StudioSettings settings) : IStudioSettingsService
    {
        public Task<StudioSettings> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task SaveAsync(StudioSettings settingsToSave, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<StudioSettings> TouchRecentProjectAsync(
            string projectPath,
            string projectName,
            CancellationToken cancellationToken) =>
            Task.FromResult(settings);
    }
}
