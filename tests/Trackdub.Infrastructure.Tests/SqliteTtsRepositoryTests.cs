using Trackdub.Domain.Projects;
using Trackdub.Domain.Tts;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteTtsRepositoryTests
{
    [Fact]
    public async Task Repositories_round_trip_voice_assignment_and_tts_take_stale_markers()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Tts.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var voiceAssignmentRepository = new SqliteVoiceAssignmentRepository(database);
            var ttsTakeRepository = new SqliteTtsTakeRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "TTS", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            var speaker = await speakerRepository.EnsureDefaultSpeakerAsync(project.Id, TestContext.Current.CancellationToken);
            VoiceAssignment assignment = VoiceAssignment.Create(project.Id, speaker.Id, "kokoro-onnx", "af_heart");
            await voiceAssignmentRepository.SaveAsync(assignment, TestContext.Current.CancellationToken);

            TtsTake take = TtsTake.Create(project.Id, assignment.Id, translatedSegmentId: null, segmentIndex: 2, "hash")
                with
            {
                Status = TtsTakeStatus.Completed,
                DurationSamples = 240,
                SampleRate = 24000,
                Provider = "fake",
                ModelId = "fake-model",
                VoiceId = "af_heart",
                DurationOverrunRatio = 0.2d,
                PreStretchDurationSeconds = 2.3d,
                StretchRatioApplied = 1.15d,
                StretchMode = TtsStretchMode.Automatic,
                StretchEngine = TtsStretchEngine.Atempo
            };
            await ttsTakeRepository.SaveAsync(take, TestContext.Current.CancellationToken);

            VoiceAssignment? reloadedAssignment = await voiceAssignmentRepository.GetAsync(project.Id, speaker.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TtsTake> takes = await ttsTakeRepository.GetByProjectAsync(project.Id, TestContext.Current.CancellationToken);
            await ttsTakeRepository.MarkBySegmentIndicesStaleAsync(project.Id, new HashSet<int> { 2 }, TestContext.Current.CancellationToken);
            TtsTake? staleTake = await ttsTakeRepository.GetAsync(take.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(reloadedAssignment);
            Assert.Equal("af_heart", reloadedAssignment!.VoiceVariant);
            TtsTake reloadedTake = Assert.Single(takes);
            Assert.Equal("fake-model", reloadedTake.ModelId);
            Assert.Equal("af_heart", reloadedTake.VoiceId);
            Assert.Equal(0.2d, reloadedTake.DurationOverrunRatio);
            Assert.Equal(2.3d, reloadedTake.PreStretchDurationSeconds);
            Assert.Equal(1.15d, reloadedTake.StretchRatioApplied);
            Assert.Equal(TtsStretchMode.Automatic, reloadedTake.StretchMode);
            Assert.Equal(TtsStretchEngine.Atempo, reloadedTake.StretchEngine);
            Assert.NotNull(staleTake);
            Assert.True(staleTake!.IsStale);
            Assert.Equal(TtsTakeStatus.Stale, staleTake.Status);
            Assert.Null(staleTake.PreStretchDurationSeconds);
            Assert.Null(staleTake.StretchRatioApplied);
            Assert.Equal(TtsStretchMode.None, staleTake.StretchMode);
            Assert.Equal(TtsStretchEngine.None, staleTake.StretchEngine);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Fallback_voice_assignments_stay_hidden_but_can_back_tts_takes()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "FallbackTts.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var voiceAssignmentRepository = new SqliteVoiceAssignmentRepository(database);
            var ttsTakeRepository = new SqliteTtsTakeRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Fallback TTS", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            var speaker = await speakerRepository.EnsureDefaultSpeakerAsync(project.Id, TestContext.Current.CancellationToken);
            VoiceAssignment fallbackAssignment = VoiceAssignment.CreateFallback(project.Id, speaker.Id, "kokoro-onnx", "af_heart");
            await voiceAssignmentRepository.SaveAsync(fallbackAssignment, TestContext.Current.CancellationToken);

            TtsTake take = TtsTake.Create(project.Id, fallbackAssignment.Id, translatedSegmentId: null, segmentIndex: 4, "fallback-hash")
                with
            {
                Status = TtsTakeStatus.Completed,
                Provider = "fake",
                ModelId = "fake-model",
                VoiceId = "af_heart"
            };
            await ttsTakeRepository.SaveAsync(take, TestContext.Current.CancellationToken);

            VoiceAssignment? visibleAssignment = await voiceAssignmentRepository.GetAsync(project.Id, speaker.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<VoiceAssignment> visibleAssignments = await voiceAssignmentRepository.GetAllAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TtsTake> takes = await ttsTakeRepository.GetByProjectAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.Null(visibleAssignment);
            Assert.Empty(visibleAssignments);
            TtsTake reloadedTake = Assert.Single(takes);
            Assert.Equal(fallbackAssignment.Id, reloadedTake.VoiceAssignmentId);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Tts_take_repository_round_trips_candidate_metadata()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "CandidateTts.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var voiceAssignmentRepository = new SqliteVoiceAssignmentRepository(database);
            var ttsTakeRepository = new SqliteTtsTakeRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Candidate TTS", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            var speaker = await speakerRepository.EnsureDefaultSpeakerAsync(project.Id, TestContext.Current.CancellationToken);
            VoiceAssignment assignment = VoiceAssignment.Create(project.Id, speaker.Id, "kokoro-onnx", "af_heart");
            await voiceAssignmentRepository.SaveAsync(assignment, TestContext.Current.CancellationToken);

            Guid candidateGroupId = Guid.NewGuid();
            TtsTake take = TtsTake.Create(project.Id, assignment.Id, translatedSegmentId: null, segmentIndex: 7, "candidate-hash") with
            {
                Status = TtsTakeStatus.Completed,
                DurationSamples = 240,
                SampleRate = 24000,
                Provider = "fake",
                CandidateGroupId = candidateGroupId,
                CandidateIndex = 2,
                Variant = TtsCandidateVariant.Candidate
            };
            await ttsTakeRepository.SaveAsync(take, TestContext.Current.CancellationToken);

            TtsTake reloaded = Assert.Single(await ttsTakeRepository.GetByProjectAsync(project.Id, TestContext.Current.CancellationToken));
            Assert.Equal(candidateGroupId, reloaded.CandidateGroupId);
            Assert.Equal(2, reloaded.CandidateIndex);
            Assert.Equal(TtsCandidateVariant.Candidate, reloaded.Variant);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
