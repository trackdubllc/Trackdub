using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Tests;

public sealed class ProjectUiSettingsMapperTests
{
    [Fact]
    public void CreateProjectUiSettings_round_trips_mix_and_export()
    {
        var mix = new MixGainSettings(
            SourceGainDb: -2d,
            DubbedSpeechGainDb: 1d,
            DuckingGainDb: -10d,
            DuckingGainExplicit: true,
            RestoreOriginalPan: true);
        var export = new ProjectExportSettings(
            [ExportSubtitleFormat.Vtt],
            ExportSubtitleSource.Bilingual,
            BurnInSubtitles: true,
            TargetLufs: -16d,
            ExportOutputContainer.Mkv,
            MatchOriginalLoudness: true);

        ProjectUiSettings settings = ProjectUiSettingsMapper.CreateProjectUiSettings(mix, export);
        MixGainSettings readMix = ProjectUiSettingsMapper.ReadMixSettings(settings);
        ProjectExportSettings? readExport = ProjectUiSettingsMapper.ReadExportSettings(settings);

        Assert.Equal(-2d, readMix.SourceGainDb);
        Assert.True(readMix.RestoreOriginalPan);
        Assert.NotNull(readExport);
        Assert.Contains(ExportSubtitleFormat.Vtt, readExport!.SubtitleFormats!);
        Assert.Equal(ExportSubtitleSource.Bilingual, readExport.SubtitleSource);
        Assert.Equal(ExportOutputContainer.Mkv, readExport.Container);
    }

    [Fact]
    public void Studio_export_settings_map_to_project_export_settings()
    {
        var studio = new StudioExportSettings(
            Srt: true,
            Vtt: false,
            Ass: false,
            BurnInSubtitles: false,
            TargetLufs: ExportLoudnessTargets.OnlineLufs,
            Container: StudioExportSettings.Mp4Container,
            SubtitleSource: StudioExportSettings.TranscriptSubtitleSource);

        ProjectExportSettings export = ProjectUiSettingsMapper.FromStudioExportSettings(studio);
        StudioExportSettings roundTrip = ProjectUiSettingsMapper.ToStudioExportSettings(export);

        Assert.True(roundTrip.Srt);
        Assert.False(roundTrip.Vtt);
        Assert.Equal(StudioExportSettings.TranscriptSubtitleSource, roundTrip.SubtitleSource);
    }

    [Fact]
    public void TryBuildPreviewMixRequest_honors_explicit_ducking_and_pan()
    {
        Guid projectId = Guid.NewGuid();
        var mix = new MixGainSettings(
            SourceGainDb: -1d,
            DubbedSpeechGainDb: 3d,
            DuckingGainDb: -8d,
            DuckingGainExplicit: true,
            RestoreOriginalPan: true);

        PreviewMixStageRequest? request = ProjectUiSettingsMapper.TryBuildPreviewMixRequest(
            new PreviewMixBuildInputs(projectId, 0d, 12d, mix));

        Assert.NotNull(request);
        Assert.Equal(projectId, request.ProjectId);
        Assert.Equal(-8d, request.DuckingGainDb);
        Assert.True(request.RestoreOriginalPan);
    }

    [Fact]
    public void CreateProjectUiSettings_round_trips_timeline_placements()
    {
        var mix = new MixGainSettings();
        var placements = new[]
        {
            new ProjectTimelineMediaPlacement("  C:\\media\\clip.wav  ", "  Intro  ", 1, -1d, 0d),
        };

        ProjectUiSettings settings = ProjectUiSettingsMapper.CreateProjectUiSettings(mix, export: null, placements);
        IReadOnlyList<ProjectTimelineMediaPlacement> read = ProjectUiSettingsMapper.ReadTimelinePlacements(settings);

        Assert.Single(read);
        ProjectTimelineMediaPlacement placement = read[0];
        Assert.Equal(@"C:\media\clip.wav", placement.MediaPath);
        Assert.Equal("Intro", placement.DisplayName);
        Assert.Equal(1, placement.TrackIndex);
        Assert.Equal(0d, placement.StartSeconds);
        Assert.Equal(0.1, placement.DurationSeconds);
    }
    [Fact]
    public void CreateProjectUiSettings_round_trips_pipeline_settings()
    {
        var mix = new MixGainSettings();
        var pipeline = new ProjectPipelineSettings(EnableAsrTextRefinement: true);

        ProjectUiSettings settings = ProjectUiSettingsMapper.CreateProjectUiSettings(
            mix,
            export: null,
            pipeline: pipeline);
        ProjectPipelineSettings read = ProjectUiSettingsMapper.ReadPipelineSettings(settings);

        Assert.True(read.EnableAsrTextRefinement);
    }

}
