using Trackdub.Contracts;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Tests;

public sealed class ExportManifestBuilderTests
{
    [Fact]
    public void Build_records_reference_clip_for_voice_cloned_segments_only()
    {
        Guid projectId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid stockSegmentId = Guid.NewGuid();
        Guid clonedSegmentId = Guid.NewGuid();
        Guid referenceClipArtifactId = Guid.NewGuid();
        var stockSegment = TranslatedSegment.Create(
            translationRevisionId,
            0,
            0,
            1,
            "Stock text.");
        var clonedSegment = TranslatedSegment.Create(
            translationRevisionId,
            1,
            1,
            2,
            "Cloned text.");
        stockSegment = stockSegment with { Id = stockSegmentId };
        clonedSegment = clonedSegment with { Id = clonedSegmentId };
        TtsTake stockTake = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            stockSegmentId,
            segmentIndex: 0) with
        {
            ArtifactId = Guid.NewGuid(),
            StageRunId = Guid.NewGuid(),
            Status = TtsTakeStatus.Completed,
            Provider = "fake",
            ModelId = "kokoro",
            VoiceId = "af_heart",
            DurationSamples = 1000,
            SampleRate = 1000
        };
        TtsTake clonedTake = TtsTake.CreateVoiceCloned(
            projectId,
            Guid.NewGuid(),
            referenceClipArtifactId,
            clonedSegmentId,
            segmentIndex: 1) with
        {
            ArtifactId = Guid.NewGuid(),
            StageRunId = Guid.NewGuid(),
            Status = TtsTakeStatus.Completed,
            Provider = "fake",
            ModelId = "chatterbox",
            VoiceId = "voice-clone",
            DurationSamples = 1000,
            SampleRate = 1000
        };

        ExportManifest manifest = ExportManifestBuilder.Build(
            projectId,
            [stockSegment, clonedSegment],
            [stockTake, clonedTake]);

        Assert.Collection(
            manifest.Segments,
            segment =>
            {
                Assert.False(segment.UsedVoiceCloning);
                Assert.Null(segment.ReferenceClipArtifactId);
            },
            segment =>
            {
                Assert.True(segment.UsedVoiceCloning);
                Assert.Equal(referenceClipArtifactId, segment.ReferenceClipArtifactId);
            });
    }

    [Fact]
    public void Build_records_stage_runs_models_voices_outputs_and_loudness()
    {
        Guid projectId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid segmentId = Guid.NewGuid();
        Guid ttsStageRunId = Guid.NewGuid();
        Guid exportStageRunId = Guid.NewGuid();
        var translatedSegment = TranslatedSegment.Create(
            translationRevisionId,
            0,
            0,
            1,
            "Hola") with
        {
            Id = segmentId
        };
        TtsTake take = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            segmentId,
            segmentIndex: 0) with
        {
            ArtifactId = Guid.NewGuid(),
            StageRunId = ttsStageRunId,
            Status = TtsTakeStatus.Completed,
            ModelId = "kokoro",
            VoiceId = "af_heart"
        };
        StageRunRecord translationRun = StageRunRecord
            .Start(projectId, StageNames.Translation, DateTimeOffset.UtcNow)
            .Complete(DateTimeOffset.UtcNow);

        ExportManifest manifest = ExportManifestBuilder.Build(new ExportManifestBuildRequest(
            projectId,
            [translatedSegment],
            [take],
            [translationRun],
            exportStageRunId,
            SourceLanguage: "EN",
            TargetLanguage: "ES",
            Container: ExportOutputContainer.Mkv,
            TargetLufs: -23d,
            AchievedLufs: -22.4d,
            Outputs: [new ExportManifestOutput("video", "out.mkv")],
            Warnings: ["codec warning"]));

        Assert.Equal(exportStageRunId, manifest.ExportStageRunId);
        Assert.Equal("en", manifest.SourceLanguage);
        Assert.Equal("es", manifest.TargetLanguage);
        Assert.Equal(ExportOutputContainer.Mkv, manifest.Container);
        Assert.Equal(-23d, manifest.Loudness?.TargetLufs);
        Assert.Equal(-22.4d, manifest.Loudness?.AchievedLufs);
        Assert.Contains(translationRun.Id, manifest.StageRunIds);
        Assert.Contains(exportStageRunId, manifest.StageRunIds);
        Assert.Contains("kokoro", manifest.ModelIds);
        Assert.Contains("af_heart", manifest.TtsVoices);
        Assert.Contains(manifest.Outputs, output => output.Kind == "video" && output.Path == "out.mkv");
        Assert.Equal("codec warning", Assert.Single(manifest.Warnings));
        Assert.Equal(ttsStageRunId, Assert.Single(manifest.Segments).StageRunId);
    }

    [Fact]
    public void Build_records_models_and_voices_from_selected_latest_takes_only()
    {
        Guid projectId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid segmentId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var translatedSegment = TranslatedSegment.Create(
            translationRevisionId,
            0,
            0,
            1,
            "Hola") with
        {
            Id = segmentId
        };
        TtsTake oldTake = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            segmentId,
            segmentIndex: 0) with
        {
            Status = TtsTakeStatus.Completed,
            ModelId = "old-model",
            VoiceId = "old-voice",
            CreatedAtUtc = now.AddMinutes(-5)
        };
        TtsTake selectedTake = oldTake with
        {
            Id = Guid.NewGuid(),
            ModelId = "selected-model",
            VoiceId = "selected-voice",
            CreatedAtUtc = now
        };
        TtsTake unrelatedTake = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            segmentIndex: 2) with
        {
            Status = TtsTakeStatus.Completed,
            ModelId = "unused-model",
            VoiceId = "unused-voice",
            CreatedAtUtc = now.AddMinutes(1)
        };

        ExportManifest manifest = ExportManifestBuilder.Build(
            projectId,
            [translatedSegment],
            [oldTake, selectedTake, unrelatedTake]);

        Assert.Equal(["selected-model"], manifest.ModelIds);
        Assert.Equal(["selected-voice"], manifest.TtsVoices);
        Assert.Equal(selectedTake.Id, Assert.Single(manifest.Segments).TtsTakeId);
    }

    [Fact]
    public void Build_excludes_provenance_for_segments_not_rendered()
    {
        Guid projectId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid renderedSegmentId = Guid.NewGuid();
        Guid staleSegmentId = Guid.NewGuid();
        var renderedSegment = TranslatedSegment.Create(
            translationRevisionId,
            0,
            0,
            1,
            "Rendered text.") with
        {
            Id = renderedSegmentId
        };
        var staleSegment = TranslatedSegment.Create(
            translationRevisionId,
            5,
            5,
            6,
            "Stale text.") with
        {
            Id = staleSegmentId
        };
        TtsTake renderedTake = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            renderedSegmentId,
            segmentIndex: 0) with
        {
            Status = TtsTakeStatus.Completed,
            ModelId = "rendered-model",
            VoiceId = "rendered-voice"
        };
        TtsTake staleTake = TtsTake.CreateStock(
            projectId,
            Guid.NewGuid(),
            staleSegmentId,
            segmentIndex: 5) with
        {
            Status = TtsTakeStatus.Completed,
            ModelId = "stale-model",
            VoiceId = "stale-voice"
        };

        ExportManifest manifest = ExportManifestBuilder.Build(new ExportManifestBuildRequest(
            projectId,
            [renderedSegment, staleSegment],
            [renderedTake, staleTake],
            StageRuns: [],
            RenderedSegmentIndices: [0]));

        Assert.Equal(["rendered-model"], manifest.ModelIds);
        Assert.Equal(["rendered-voice"], manifest.TtsVoices);
        ExportManifestSegment segment = Assert.Single(manifest.Segments);
        Assert.Equal(0, segment.SegmentIndex);
        Assert.Equal(renderedTake.Id, segment.TtsTakeId);
    }
}
