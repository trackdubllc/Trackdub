using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;
using System.Security.Cryptography;

#pragma warning disable CS0618

namespace Trackdub.Application.Tests;

public partial class TranscriptProjectServiceTests
{
    [Fact]
    public async Task SpeechAudioEnhancement_wired_enhancement_handler_runs_and_produces_artifact()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, enableSpeechEnhancement: true);
        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Speech Enhancement Test", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, scope.SpeechAudioEnhancementService.CallCount);
        Assert.Contains(result.ProjectState.Artifacts, a => a.Kind == ArtifactKind.SpeechEnhancedAudio);
    }

    [Fact]
    public async Task SpeechAudioEnhancement_failure_is_nonfatal_and_transcript_is_still_generated()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var enhancementService = new FakeSpeechAudioEnhancementService { ThrowOnEnhance = true };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            speechAudioEnhancementService: enhancementService,
            enableSpeechEnhancement: true);
        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Speech Enhancement Fail Test", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, enhancementService.CallCount);
        Assert.NotNull(result.CurrentTranscriptRevision);
        Assert.DoesNotContain(result.ProjectState.Artifacts, a => a.Kind == ArtifactKind.SpeechEnhancedAudio);
    }

    [Fact]
    public async Task SpeechAudioEnhancement_disabled_handler_not_called_and_no_enhanced_artifact()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, enableSpeechEnhancement: false);
        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Speech Enhancement Disabled Test", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, scope.SpeechAudioEnhancementService.CallCount);
        Assert.DoesNotContain(result.ProjectState.Artifacts, a => a.Kind == ArtifactKind.SpeechEnhancedAudio);
    }

    [Fact]
    public async Task SpeechAudioEnhancement_prep_analyzes_enhanced_audio_when_enhancement_succeeds()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, enableSpeechEnhancement: true);
        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Speech Enhancement Prep Input Test", sourcePath),
            TestContext.Current.CancellationToken);

        ProjectArtifact enhanced = Assert.Single(
            result.ProjectState.Artifacts,
            artifact => artifact.Kind == ArtifactKind.SpeechEnhancedAudio);
        ProjectArtifact normalized = TranscriptWorkflowUtilities.GetLatestArtifactByKind(
            result.ProjectState.Artifacts,
            ArtifactKind.NormalizedAudio)
            ?? throw new InvalidOperationException("Expected normalized audio artifact.");
        string enhancedPath = scope.ArtifactStore.GetPath(enhanced.RelativePath);
        string normalizedPath = scope.ArtifactStore.GetPath(normalized.RelativePath);

        Assert.Contains(
            scope.AudioQualityAnalyzer.Requests,
            request => string.Equals(request.AudioPath, enhancedPath, StringComparison.OrdinalIgnoreCase)
                && request.SourceKind == SpeechAudioSourceKind.FullMix);
        Assert.DoesNotContain(
            scope.AudioQualityAnalyzer.Requests,
            request => string.Equals(request.AudioPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                && request.SourceKind == SpeechAudioSourceKind.FullMix);
    }
}
