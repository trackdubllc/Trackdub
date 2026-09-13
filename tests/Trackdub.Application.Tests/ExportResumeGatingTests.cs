using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Drives the export resume decision (StageArtifactResumeEvaluator.CanResumeStageAsync) end to
/// end: a completed export run + present output + a persisted ExportManifest whose gating flags
/// are compared against the current run's execution snapshot. These tests fail if the wiring that
/// reads the persisted gating flags is reverted, unlike the snapshot-shape tests that only assert
/// the captured dictionary.
/// </summary>
public sealed class ExportResumeGatingTests
{
    [Fact]
    public async Task CanResumeStageAsync_returns_true_when_gating_flags_match()
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportRelativePath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Theory]
    [InlineData(ExportResumeGating.ApplyTimbrePolishKey, "False")]
    [InlineData(ExportResumeGating.RestoreOriginalPanKey, "True")]
    [InlineData(ExportResumeGating.MatchOriginalLoudnessKey, "True")]
    [InlineData(ExportResumeGating.BurnInSubtitlesKey, "True")]
    [InlineData(ExportResumeGating.VideoEncoderKey, "nvenc")]
    [InlineData(ExportResumeGating.SubtitleSourceKey, "Transcript")]
    [InlineData(ExportResumeGating.SubtitleFormatsKey, "Vtt")]
    [InlineData(ExportResumeGating.ExportFormatKey, "mkv")]
    public async Task CanResumeStageAsync_returns_false_when_a_gating_flag_changes(string key, string changedValue)
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        Dictionary<string, string> snapshot = BaselineSnapshot();
        snapshot[key] = changedValue;

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportRelativePath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_true_for_equivalent_subtitle_source_and_formats()
    {
        using var temp = new TempDir();
        // Prior run recorded the resolved translated source and the resolved [Srt] format.
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        // Current snapshot built from the raw options: SubtitleSource null (== "translated")
        // and SubtitleFormats ["SRT"] (case-insensitively == the resolved "Srt" token).
        Dictionary<string, string> snapshot = new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportResumeGating_ResolveSubtitleSource(null),
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["SRT"]),
            videoEncoder: VideoEncoderPreference.Auto), StringComparer.OrdinalIgnoreCase);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportRelativePath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_falls_back_to_resume_when_manifest_has_no_gating()
    {
        using var temp = new TempDir();
        // Older project: manifest predates the gating field (Gating == null).
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, gating: null);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportRelativePath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_false_when_export_output_missing()
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(
            temp.Path,
            BaselineGating(),
            writeExportOutput: false);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportRelativePath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    private static ExportSubtitleSource ExportResumeGating_ResolveSubtitleSource(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "transcript" => ExportSubtitleSource.Transcript,
            "bilingual" => ExportSubtitleSource.Bilingual,
            _ => ExportSubtitleSource.Translated,
        };

    private static ExportManifestGating BaselineGating() =>
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromResolvedFormats([ExportSubtitleFormat.Srt]),
            videoEncoder: VideoEncoderPreference.Auto));

    private static Dictionary<string, string> BaselineSnapshot() =>
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["srt"]),
            videoEncoder: VideoEncoderPreference.Auto), StringComparer.OrdinalIgnoreCase);

    private sealed record ExportResumeFixture(
        TranscriptProjectState State,
        FakeArtifactStore ArtifactStore,
        string ExportRelativePath)
    {
        public static async Task<ExportResumeFixture> CreateAsync(
            string projectRootPath,
            ExportManifestGating? gating,
            bool writeExportOutput = true)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid projectId = Guid.NewGuid();
            Guid mediaAssetId = Guid.NewGuid();
            Guid transcriptRevisionId = Guid.NewGuid();
            var project = new TrackdubProject(projectId, "Export", now, now);

            string sourcePath = Path.Combine(projectRootPath, "source.mp4");
            Directory.CreateDirectory(projectRootPath);
            await File.WriteAllBytesAsync(sourcePath, [0, 0, 0, 24]);

            var mediaAsset = new MediaAsset(
                mediaAssetId,
                projectId,
                sourcePath,
                Path.GetFileName(sourcePath),
                "source-hash",
                100,
                now,
                "mp4",
                6.0d,
                HasAudio: true,
                HasVideo: true,
                now);

            TranscriptSegment transcriptSegment = TranscriptSegment.Create(
                transcriptRevisionId,
                0,
                1.0d,
                2.0d,
                "Hello",
                Guid.NewGuid(),
                "en");

            StageRunRecord exportRun = StageRunRecord
                .Start(projectId, StageNames.Export, now)
                .Complete(now.AddSeconds(1));

            var artifactStore = new FakeArtifactStore(projectRootPath);

            var manifest = new ExportManifest(
                projectId,
                now,
                exportRun.Id,
                SourceLanguage: "en",
                TargetLanguage: "es",
                Container: ExportOutputContainer.Mp4,
                Loudness: null,
                StageRunIds: [exportRun.Id],
                ModelIds: [],
                TtsVoices: [],
                Outputs: [],
                Warnings: [],
                Segments: [],
                Gating: gating);
            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.GetExportManifestRelativePath(exportRun.Id),
                manifest,
                CancellationToken.None);

            string exportRelativePath = Path.Combine("exports", "dubbed.mp4")
                .Replace(Path.DirectorySeparatorChar, '/');
            if (writeExportOutput)
            {
                string exportFullPath = Path.Combine(projectRootPath, "exports", "dubbed.mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(exportFullPath)!);
                await File.WriteAllBytesAsync(exportFullPath, [1, 2, 3, 4]);
            }

            var openResult = new OpenProjectResult(
                project,
                mediaAsset,
                new SourceMediaReference(
                    mediaAsset.SourceFilePath,
                    mediaAsset.SourceFileName,
                    new FileFingerprint(mediaAsset.FingerprintSha256, mediaAsset.SourceSizeBytes, mediaAsset.SourceLastWriteTimeUtc),
                    ProbeSnapshot(mediaAsset.DurationSeconds),
                    DateTimeOffset.UtcNow),
                SourceMediaStatus.Available,
                SourceStatusMessage: null,
                Artifacts: [],
                TranscriptLanguage: "en");
            TranscriptRevision transcriptRevision = TranscriptRevision.Create(
                projectId,
                stageRunId: null,
                revisionNumber: 1,
                DateTimeOffset.UtcNow);
            TranslationRevision translationRevision = TranslationRevision.Create(
                projectId,
                stageRunId: null,
                transcriptRevision.Id,
                "es",
                revisionNumber: 1,
                DateTimeOffset.UtcNow);

            var state = new TranscriptProjectState(
                openResult,
                transcriptRevision,
                [transcriptSegment],
                Speakers: [],
                SpeakerTurns: [],
                translationRevision,
                TranslatedSegments: [],
                IsTranslationStale: false,
                TranscriptLanguage: "en",
                StageRuns: [exportRun],
                SupportedTargetLanguages: [],
                SelectedTranslationTargetLanguage: "es",
                StaleTranslatedSegmentIndices: new HashSet<int>(),
                WaveformSummary: null,
                AvailableVoices: [],
                VoiceAssignments: [],
                TtsTakes: [],
                TtsSegmentStates: [],
                VoiceAssignmentWarnings: []);

            return new ExportResumeFixture(state, artifactStore, exportRelativePath);
        }

        private static MediaProbeSnapshot ProbeSnapshot(double durationSeconds) =>
            new(
                "mp4",
                "MP4",
                durationSeconds,
                BitRate: null,
                AudioStreams: [new MediaAudioStream(0, "aac", Channels: 2, SampleRate: 48000, durationSeconds)],
                VideoStreams: [new MediaVideoStream(1, "h264", Width: 1920, Height: 1080, FrameRate: 24.0d, durationSeconds)],
                SubtitleStreams: []);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trackdub-export-resume-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
