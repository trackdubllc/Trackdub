using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Covers <see cref="TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations"/> — the
/// mechanism that surfaces speech-enhancement failures (which are caught internally by
/// TryPrepareSpeechAudioAsync and fall back to unenhanced audio) as DegradationRecords on
/// the separation StageOutcome rather than silently discarding them (P2-11).
/// </summary>
public sealed class SpeechEnhancementDegradationTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public void NoSpeechEnhancementRuns_ReturnsNull()
    {
        TranscriptProjectState state = BuildState([]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.Null(result);
    }

    [Fact]
    public void CompletedSpeechEnhancement_ReturnsNull()
    {
        StageRunRecord completed = StageRunRecord.Start(ProjectId, StageNames.SpeechEnhancement, DateTimeOffset.UtcNow)
            with
        { Status = StageRunStatus.Completed };
        TranscriptProjectState state = BuildState([completed]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.Null(result);
    }

    [Fact]
    public void FailedSpeechEnhancement_ReturnsNonNullDegradation()
    {
        StageRunRecord failed = StageRunRecord.Start(ProjectId, StageNames.SpeechEnhancement, DateTimeOffset.UtcNow)
            with
        { Status = StageRunStatus.Failed, FailureReason = "feat_spec: Got 481 Expected 96" };
        TranscriptProjectState state = BuildState([failed]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Contains("speech-enhancement failed", result[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("feat_spec: Got 481 Expected 96", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FailedSpeechEnhancement_NullReason_ReturnsGenericMessage()
    {
        StageRunRecord failed = StageRunRecord.Start(ProjectId, StageNames.SpeechEnhancement, DateTimeOffset.UtcNow)
            with
        { Status = StageRunStatus.Failed, FailureReason = null };
        TranscriptProjectState state = BuildState([failed]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Contains("speech-enhancement failed", result[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyMostRecentFailure_ReturnsOneDegradation()
    {
        DateTimeOffset earlier = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset later = DateTimeOffset.UtcNow;

        StageRunRecord olderFail = StageRunRecord.Start(ProjectId, StageNames.SpeechEnhancement, earlier)
            with
        { Status = StageRunStatus.Failed, FailureReason = "old error" };
        StageRunRecord newerFail = StageRunRecord.Start(ProjectId, StageNames.SpeechEnhancement, later)
            with
        { Status = StageRunStatus.Failed, FailureReason = "new error" };

        TranscriptProjectState state = BuildState([olderFail, newerFail]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Contains("new error", result[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SeparationStageRuns_Ignored_OnlyEnhancementCounted()
    {
        StageRunRecord separationFailed = StageRunRecord.Start(ProjectId, StageNames.Separation, DateTimeOffset.UtcNow)
            with
        { Status = StageRunStatus.Failed, FailureReason = "unrelated" };
        TranscriptProjectState state = BuildState([separationFailed]);

        IReadOnlyList<string>? result = TrackdubDubbingEngine.ExtractSpeechEnhancementDegradations(state);

        Assert.Null(result);
    }

    private static TranscriptProjectState BuildState(IReadOnlyList<StageRunRecord> stageRuns)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(ProjectId, "Test", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(), ProjectId, "source.mp4", "source.mp4", "hash", 100, now,
            "mp4", 4.0d, HasAudio: true, HasVideo: true, now);
        var projectState = new OpenProjectResult(project, mediaAsset, null, SourceMediaStatus.Available, null, [], "en");

        return new TranscriptProjectState(
            projectState,
            CurrentTranscriptRevision: null,
            TranscriptSegments: [],
            Speakers: [],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: null,
            StageRuns: stageRuns,
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }
}
