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
    public async Task CreateAsync_when_diarization_fails_falls_back_to_single_speaker_without_throwing()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, new ThrowingDiarizationEngine());
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        ProjectSpeaker speaker = Assert.Single(created.Speakers);
        Assert.Equal("Speaker 1", speaker.DisplayName);
        Assert.Empty(created.SpeakerTurns);
        Assert.All(created.TranscriptSegments, segment => Assert.Equal(speaker.Id, segment.SpeakerId));
        Assert.Contains(created.StageRuns, stageRun => stageRun.StageName == "diarization" && stageRun.Status == StageRunStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_propagates_commercial_safe_mode_to_diarization_engine()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var recordingEngine = new RecordingDiarizationEngine();
        FakeServiceScope scope = CreateScope(tempDirectory, recordingEngine);
        _ = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: true),
            TestContext.Current.CancellationToken);

    }

    [Fact]
    public async Task CreateAsync_when_diarization_is_enabled_transcribes_diarized_speaker_regions()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var diarizationEngine = new RecordingDiarizationEngine();
        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationEngine,
            transcriptionEngine: transcriptionEngine);

        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        SpeechRegion region = Assert.Single(transcriptionEngine.LastRegions);
        Assert.Equal(0.0, region.StartSeconds, precision: 3);
        Assert.Equal(1.5, region.EndSeconds, precision: 3);
        TranscriptSegment segment = Assert.Single(created.TranscriptSegments);
        ProjectSpeaker speaker = Assert.Single(created.Speakers);
        Assert.Equal(speaker.Id, segment.SpeakerId);
    }

    [Fact]
    public async Task CreateAsync_when_diarization_regions_are_padded_keeps_adjacent_speakers_non_overlapping()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var diarizationEngine = new RecordingDiarizationEngine(
        [
            new DiarizedSpeakerTurn("spk_0", 2.0, 3.0, Confidence: 0.9, HasOverlap: false),
            new DiarizedSpeakerTurn("spk_1", 3.2, 4.0, Confidence: 0.9, HasOverlap: false)
        ]);
        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationEngine,
            transcriptionEngine: transcriptionEngine);

        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Collection(
            transcriptionEngine.LastRegions,
            first =>
            {
                Assert.Equal(1.5, first.StartSeconds, precision: 3);
                Assert.Equal(3.1, first.EndSeconds, precision: 3);
            },
            second =>
            {
                Assert.Equal(3.1, second.StartSeconds, precision: 3);
                Assert.Equal(4.5, second.EndSeconds, precision: 3);
            });
    }

    [Fact]
    public async Task RetranscribeSegmentsAsync_replaces_selected_region_only()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: transcriptionEngine);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        TranscriptSegment selected = created.TranscriptSegments[1];

        TranscriptProjectState retranscribed = await scope.Service.RetranscribeSegmentsAsync(
            new RetranscribeTranscriptSegmentsRequest(created.CurrentTranscriptRevision!.Id, [selected.Id]),
            TestContext.Current.CancellationToken);

        SpeechRegion requestedRegion = Assert.Single(transcriptionEngine.LastRegions);
        Assert.Equal(selected.SegmentIndex, requestedRegion.Index);
        Assert.Equal(selected.StartSeconds, requestedRegion.StartSeconds, precision: 3);
        Assert.Equal(selected.EndSeconds, requestedRegion.EndSeconds, precision: 3);
        Assert.Equal(2, retranscribed.TranscriptSegments.Count);
        Assert.Equal(created.TranscriptSegments[0].Text, retranscribed.TranscriptSegments[0].Text);
        Assert.Equal(created.TranscriptSegments[0].Words.Select(word => word.Text), retranscribed.TranscriptSegments[0].Words.Select(word => word.Text));
        Assert.Equal("Recorded segment 1.", retranscribed.TranscriptSegments[1].Text);
        Assert.NotEqual(created.TranscriptSegments[1].Text, retranscribed.TranscriptSegments[1].Text);
        Assert.NotEmpty(retranscribed.TranscriptSegments[1].Words);
        Assert.All(retranscribed.TranscriptSegments[1].Words, word => Assert.True(word.Confidence is >= 0d and <= 1d));
        Assert.Equal(2, retranscribed.CurrentTranscriptRevision!.RevisionNumber);
    }


    [Fact]
    public async Task CreateAsync_uses_diarization_stage_handler_to_download_missing_model()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var recordingEngine = new RecordingDiarizationEngine();
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        string modelCacheRoot = Path.Combine(tempDirectory, "model-cache");
        var handler = new DiarizationStageHandler(
            recordingEngine,
            downloader,
            modelCacheRegistrar: registrar,
            modelCacheRoot: modelCacheRoot,
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            recordingEngine,
            diarizationStageHandler: handler);

        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("tonythethompson/diar-streaming-sortformer-4spk-v2.1-onnx", downloader.ModelId);
        Assert.Equal("onnx/model.onnx", downloader.FileName);
        Assert.NotNull(downloader.DestinationPath);
        Assert.True(File.Exists(downloader.DestinationPath));
        Assert.NotNull(registrar.Record);
        Assert.Equal("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", registrar.Record.ModelId);
        Assert.Equal(Path.Combine(modelCacheRoot, "cgus", "diar_streaming_sortformer_4spk-v2.1-onnx"), registrar.Record.RootPath);
        Assert.Equal(SortFormerTestFixtures.ExpectedSha256, registrar.Record.Sha256);
        Assert.Equal(1, recordingEngine.CallCount);
        Assert.Single(created.Speakers);
        Assert.Single(created.SpeakerTurns);
    }

    [Fact]
    public void GetRequiredDiarizationModelStatus_reports_missing_downloadable_model()
    {
        string tempDirectory = CreateTempDirectory();
        string modelCacheRoot = Path.Combine(tempDirectory, "model-cache");
        var handler = new DiarizationStageHandler(
            new RecordingDiarizationEngine(),
            new RecordingModelDownloader(),
            modelCacheRoot: modelCacheRoot,
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationStageHandler: handler);

        RequiredDiarizationModelStatus? status = scope.Service.GetRequiredDiarizationModelStatus();

        Assert.NotNull(status);
        Assert.Equal("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", status.ModelId);
        Assert.Equal("onnx/model.onnx", status.ExpectedFileName);
        Assert.False(status.IsAvailable);
        Assert.True(status.CanAutoDownload);
        Assert.False(status.RequiresOnnxExport);
        Assert.Equal("https://huggingface.co/tonythethompson/diar-streaming-sortformer-4spk-v2.1-onnx", status.SourceUrl);
        Assert.EndsWith(
            Path.Combine("cgus", "diar_streaming_sortformer_4spk-v2.1-onnx", "onnx", "model.onnx"),
            status.ModelPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportDiarizationModelAsync_copies_model_into_cache_and_registers_record()
    {
        string tempDirectory = CreateTempDirectory();
        string sourceModelPath = Path.Combine(tempDirectory, "source.onnx");
        byte[] sourceBytes = SortFormerTestFixtures.ModelBytes;
        await File.WriteAllBytesAsync(sourceModelPath, sourceBytes, TestContext.Current.CancellationToken);

        var registrar = new RecordingModelCacheRegistrar();
        string modelCacheRoot = Path.Combine(tempDirectory, "model-cache");
        var handler = new DiarizationStageHandler(
            new RecordingDiarizationEngine(),
            new RecordingModelDownloader(),
            modelCacheRegistrar: registrar,
            modelCacheRoot: modelCacheRoot,
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationStageHandler: handler);

        await scope.Service.ImportDiarizationModelAsync(sourceModelPath, TestContext.Current.CancellationToken);

        RequiredDiarizationModelStatus status = scope.Service.GetRequiredDiarizationModelStatus()
            ?? throw new InvalidOperationException("Expected diarization model status.");
        Assert.True(status.IsAvailable);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(status.ModelPath, TestContext.Current.CancellationToken));
        Assert.NotNull(registrar.Record);
        Assert.Equal("cgus/diar_streaming_sortformer_4spk-v2.1-onnx", registrar.Record.ModelId);
        Assert.Equal(Path.Combine(modelCacheRoot, "cgus", "diar_streaming_sortformer_4spk-v2.1-onnx"), registrar.Record.RootPath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(), registrar.Record.Sha256);
    }

    [Fact]
    public async Task CreateAsync_defaults_commercial_safe_mode_to_false_when_not_specified()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var recordingEngine = new RecordingDiarizationEngine();
        FakeServiceScope scope = CreateScope(tempDirectory, recordingEngine);
        _ = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

    }

    [Fact]
    public async Task CreateAsync_when_speaker_detection_is_disabled_skips_diarization_and_uses_single_speaker()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, new ThrowingDiarizationEngine());
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        ProjectSpeaker speaker = Assert.Single(created.Speakers);
        Assert.Equal("Speaker 1", speaker.DisplayName);
        Assert.Empty(created.SpeakerTurns);
        Assert.All(created.TranscriptSegments, segment => Assert.Equal(speaker.Id, segment.SpeakerId));
        Assert.DoesNotContain(created.StageRuns, stageRun => stageRun.StageName == "diarization");
    }
}
