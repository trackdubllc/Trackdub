using Trackdub.Application.Transcripts;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Tests;

public sealed class SubtitleExportServiceTests
{
    private readonly SubtitleExportService service = new();

    [Fact]
    public void Export_writes_srt_with_subrip_timestamps()
    {
        string output = NormalizeNewlines(service.Export(CreateCues(), ExportSubtitleFormat.Srt));

        Assert.Equal(
            """
            1
            00:00:01,234 --> 00:00:03,456
            Hola mundo.

            2
            01:01:01,789 --> 01:01:02,900
            Segunda linea.


            """,
            output);
    }

    [Fact]
    public void Export_writes_vtt_with_header_and_cue_timestamps()
    {
        string output = NormalizeNewlines(service.Export(CreateCues(), ExportSubtitleFormat.Vtt));

        Assert.Equal(
            """
            WEBVTT

            00:00:01.234 --> 00:00:03.456
            Hola mundo.

            01:01:01.789 --> 01:01:02.900
            Segunda linea.


            """,
            output);
    }

    [Fact]
    public void Export_writes_ass_style_block_and_centisecond_timestamps()
    {
        string output = NormalizeNewlines(service.Export(CreateCues(), ExportSubtitleFormat.Ass));

        Assert.Contains("[V4+ Styles]\n", output);
        Assert.Contains("Style: Default,Arial,48,", output);
        Assert.Contains("[Events]\n", output);
        Assert.Contains("Dialogue: 0,0:00:01.23,0:00:03.46,Default,,0,0,0,,Hola mundo.\n", output);
        Assert.Contains("Dialogue: 0,1:01:01.79,1:01:02.90,Default,,0,0,0,,Segunda linea.\n", output);
    }

    [Fact]
    public void Export_writes_ass_with_custom_style_options()
    {
        string output = NormalizeNewlines(service.Export(
            CreateCues(),
            ExportSubtitleFormat.Ass,
            new AssSubtitleStyleOptions(PlayResX: 1280, PlayResY: 720, FontSize: 36, FontName: "Segoe UI", MarginV: 42)));

        Assert.Contains("PlayResX: 1280\n", output);
        Assert.Contains("PlayResY: 720\n", output);
        Assert.Contains("Style: Default,Segoe UI,36,", output);
        Assert.Contains(",80,80,42,1\n", output);
    }

    [Fact]
    public void Export_normalizes_multiline_cue_text_to_format_newlines()
    {
        SubtitleCue[] cues = [new(Guid.NewGuid(), 0, 0d, 1d, "First\r\nSecond\rThird")];

        string srt = service.Export(cues, ExportSubtitleFormat.Srt);
        string vtt = service.Export(cues, ExportSubtitleFormat.Vtt);
        string ass = NormalizeNewlines(service.Export(cues, ExportSubtitleFormat.Ass));

        Assert.Contains($"First{Environment.NewLine}Second{Environment.NewLine}Third", srt);
        Assert.Contains($"First{Environment.NewLine}Second{Environment.NewLine}Third", vtt);
        Assert.Contains("First\\NSecond\\NThird", ass);
    }

    [Fact]
    public void BuildTranscriptCues_uses_source_language_transcript_text()
    {
        Guid revisionId = Guid.NewGuid();
        TranscriptSegment segment = TranscriptSegment.Create(
            revisionId,
            0,
            0d,
            1d,
            "Source words",
            detectedLanguage: "en-US");

        SubtitleCue cue = Assert.Single(service.BuildTranscriptCues([segment]));

        Assert.Equal("Source words", cue.Text);
        Assert.Equal(segment.Id, cue.SegmentId);
    }

    [Fact]
    public void Export_rounds_srt_and_vtt_half_milliseconds_away_from_zero()
    {
        SubtitleCue[] cues = [new(Guid.NewGuid(), 0, 1.2345d, 2.3455d, "Rounded.")];

        string srt = NormalizeNewlines(service.Export(cues, ExportSubtitleFormat.Srt));
        string vtt = NormalizeNewlines(service.Export(cues, ExportSubtitleFormat.Vtt));

        Assert.Contains("00:00:01,235 --> 00:00:02,346", srt);
        Assert.Contains("00:00:01.235 --> 00:00:02.346", vtt);
    }

    [Fact]
    public void BuildTranslatedCues_uses_dubbed_translation_text()
    {
        Guid revisionId = Guid.NewGuid();
        TranslatedSegment segment = TranslatedSegment.Create(
            revisionId,
            0,
            0d,
            1d,
            "Translated words");

        SubtitleCue cue = Assert.Single(service.BuildTranslatedCues([segment]));

        Assert.Equal("Translated words", cue.Text);
        Assert.Equal(segment.Id, cue.SegmentId);
    }

    [Fact]
    public void BuildBilingualCues_puts_source_first_and_translated_second_with_fallbacks()
    {
        Guid transcriptRevisionId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        TranscriptSegment sourceWithTranslation = TranscriptSegment.Create(
            transcriptRevisionId,
            0,
            0d,
            1d,
            "Source words",
            detectedLanguage: "en-US");
        TranscriptSegment sourceOnly = TranscriptSegment.Create(
            transcriptRevisionId,
            1,
            1d,
            2d,
            "Source only",
            detectedLanguage: "en-US");
        TranslatedSegment translated = TranslatedSegment.Create(
            translationRevisionId,
            0,
            0d,
            1d,
            "Translated words");

        IReadOnlyList<SubtitleCue> cues = service.BuildBilingualCues(
            [sourceWithTranslation, sourceOnly],
            [translated]);

        Assert.Collection(
            cues,
            cue => Assert.Equal("Source words\nTranslated words", cue.Text),
            cue => Assert.Equal("Source only", cue.Text));
    }

    private static SubtitleCue[] CreateCues() =>
    [
        new(Guid.NewGuid(), 0, 1.234d, 3.456d, "Hola mundo."),
        new(Guid.NewGuid(), 1, 3661.789d, 3662.9d, "Segunda linea.")
    ];

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
