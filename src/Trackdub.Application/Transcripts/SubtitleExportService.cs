using System.Globalization;
using System.Text;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;

namespace Trackdub.Application.Transcripts;

public sealed class SubtitleExportService
{
    public string Export(
        IReadOnlyList<SubtitleCue> cues,
        ExportSubtitleFormat format,
        AssSubtitleStyleOptions? assStyleOptions = null)
    {
        return ExportNormalized(NormalizeForExport(cues), format, assStyleOptions);
    }

    public IReadOnlyList<SubtitleCue> NormalizeForExport(IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return NormalizeCues(cues);
    }

    public string ExportNormalized(
        IReadOnlyList<SubtitleCue> normalizedCues,
        ExportSubtitleFormat format,
        AssSubtitleStyleOptions? assStyleOptions = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedCues);
        return format switch
        {
            ExportSubtitleFormat.Srt => ExportSrt(normalizedCues),
            ExportSubtitleFormat.Vtt => ExportVtt(normalizedCues),
            ExportSubtitleFormat.Ass => ExportAss(normalizedCues, assStyleOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(format), "Unsupported subtitle format.")
        };
    }

    public IReadOnlyList<SubtitleCue> BuildTranslatedCues(IReadOnlyList<TranslatedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments
            .Where(static segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(static segment => segment.SegmentIndex)
            .Select(static segment => new SubtitleCue(
                segment.Id,
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text))
            .ToArray();
    }

    public IReadOnlyList<SubtitleCue> BuildTranscriptCues(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments
            .Where(static segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(static segment => segment.SegmentIndex)
            .Select(static segment => new SubtitleCue(
                segment.Id,
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text))
            .ToArray();
    }

    public IReadOnlyList<SubtitleCue> BuildBilingualCues(
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<TranslatedSegment> translatedSegments)
    {
        ArgumentNullException.ThrowIfNull(transcriptSegments);
        ArgumentNullException.ThrowIfNull(translatedSegments);

        Dictionary<int, TranslatedSegment> translatedByIndex = translatedSegments
            .Where(static segment => !string.IsNullOrWhiteSpace(segment.Text))
            .GroupBy(static segment => segment.SegmentIndex)
            .ToDictionary(static group => group.Key, static group => group.Last());
        return transcriptSegments
            .OrderBy(static segment => segment.SegmentIndex)
            .Select(segment =>
            {
                string sourceText = segment.Text.Trim();
                string translatedText = translatedByIndex.TryGetValue(segment.SegmentIndex, out TranslatedSegment? translated)
                    ? translated.Text.Trim()
                    : string.Empty;
                string text = BuildBilingualText(sourceText, translatedText);
                return new SubtitleCue(
                    segment.Id,
                    segment.SegmentIndex,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    text);
            })
            .Where(static cue => !string.IsNullOrWhiteSpace(cue.Text))
            .ToArray();
    }

    private static string ExportSrt(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        int cueNumber = 1;
        foreach (SubtitleCue cue in cues)
        {
            builder.AppendLine(cueNumber.ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatSrtTime(cue.StartSeconds));
            builder.Append(" --> ");
            builder.AppendLine(FormatSrtTime(cue.EndSeconds));
            builder.AppendLine(NormalizeSubtitleText(cue.Text));
            builder.AppendLine();
            cueNumber++;
        }

        return builder.ToString();
    }

    private static string ExportVtt(IReadOnlyList<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WEBVTT");
        builder.AppendLine();
        foreach (SubtitleCue cue in cues)
        {
            builder.Append(FormatVttTime(cue.StartSeconds));
            builder.Append(" --> ");
            builder.AppendLine(FormatVttTime(cue.EndSeconds));
            builder.AppendLine(NormalizeSubtitleText(cue.Text));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ExportAss(IReadOnlyList<SubtitleCue> cues, AssSubtitleStyleOptions? styleOptions)
    {
        AssSubtitleStyleOptions style = NormalizeAssStyle(styleOptions);
        var builder = new StringBuilder();
        builder.AppendLine("[Script Info]");
        builder.AppendLine("ScriptType: v4.00+");
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"PlayResX: {style.PlayResX}"));
        builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"PlayResY: {style.PlayResY}"));
        builder.AppendLine();
        builder.AppendLine("[V4+ Styles]");
        builder.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Style: Default,{style.FontName},{style.FontSize},&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,1,2,{style.MarginL},{style.MarginR},{style.MarginV},1"));
        builder.AppendLine();
        builder.AppendLine("[Events]");
        builder.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (SubtitleCue cue in cues)
        {
            builder.Append("Dialogue: 0,");
            builder.Append(FormatAssTime(cue.StartSeconds));
            builder.Append(',');
            builder.Append(FormatAssTime(cue.EndSeconds));
            builder.Append(",Default,,0,0,0,,");
            builder.AppendLine(EscapeAssText(cue.Text));
        }

        return builder.ToString();
    }

    private static IReadOnlyList<SubtitleCue> NormalizeCues(IReadOnlyList<SubtitleCue> cues) =>
        cues
            .Where(static cue => !string.IsNullOrWhiteSpace(cue.Text))
            .OrderBy(static cue => cue.StartSeconds)
            .ThenBy(static cue => cue.SegmentIndex)
            .Select(static cue =>
            {
                double start = NormalizeSeconds(cue.StartSeconds);
                double end = Math.Max(start, NormalizeSeconds(cue.EndSeconds));
                return cue with
                {
                    StartSeconds = start,
                    EndSeconds = end,
                    Text = cue.Text.Trim()
                };
            })
            .ToArray();

    private static string NormalizeSubtitleText(string text, string? newline = null)
    {
        string normalizedNewline = newline ?? Environment.NewLine;
        return text
            .Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", normalizedNewline, StringComparison.Ordinal);
    }

    private static string BuildBilingualText(string sourceText, string translatedText)
    {
        bool hasSource = !string.IsNullOrWhiteSpace(sourceText);
        bool hasTranslated = !string.IsNullOrWhiteSpace(translatedText);
        return (hasSource, hasTranslated) switch
        {
            (true, true) => $"{sourceText}\n{translatedText}",
            (true, false) => sourceText,
            (false, true) => translatedText,
            _ => string.Empty
        };
    }

    private static string EscapeAssText(string text) =>
        NormalizeSubtitleText(text, "\n")
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("{", @"\{", StringComparison.Ordinal)
            .Replace("}", @"\}", StringComparison.Ordinal)
            .Replace("\n", @"\N", StringComparison.Ordinal);

    private static double NormalizeSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds > 0d ? seconds : 0d;

    private static AssSubtitleStyleOptions NormalizeAssStyle(AssSubtitleStyleOptions? options)
    {
        AssSubtitleStyleOptions style = options ?? new AssSubtitleStyleOptions();
        string fontName = string.IsNullOrWhiteSpace(style.FontName)
            ? "Arial"
            : style.FontName.Replace(',', ' ').Trim();
        return style with
        {
            PlayResX = Math.Max(1, style.PlayResX),
            PlayResY = Math.Max(1, style.PlayResY),
            FontSize = Math.Max(1, style.FontSize),
            FontName = fontName,
            MarginL = Math.Max(0, style.MarginL),
            MarginR = Math.Max(0, style.MarginR),
            MarginV = Math.Max(0, style.MarginV)
        };
    }

    private static string FormatSrtTime(double seconds) => FormatClock(seconds, ',', includeHoursPadding: true, millisecondDigits: 3);

    private static string FormatVttTime(double seconds) => FormatClock(seconds, '.', includeHoursPadding: true, millisecondDigits: 3);

    private static string FormatAssTime(double seconds)
    {
        long totalCentiseconds = (long)Math.Round(NormalizeSeconds(seconds) * 100d, MidpointRounding.AwayFromZero);
        long totalSeconds = totalCentiseconds / 100;
        long centiseconds = totalCentiseconds % 100;
        long totalMinutes = totalSeconds / 60;
        long displaySeconds = totalSeconds % 60;
        long totalHours = totalMinutes / 60;
        long displayMinutes = totalMinutes % 60;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalHours}:{displayMinutes:00}:{displaySeconds:00}.{centiseconds:00}");
    }

    private static string FormatClock(
        double seconds,
        char separator,
        bool includeHoursPadding,
        int millisecondDigits)
    {
        TimeSpan value = TimeSpan.FromMilliseconds(Math.Round(
            NormalizeSeconds(seconds) * 1000d,
            MidpointRounding.AwayFromZero));
        int totalHours = (int)value.TotalHours;
        string hours = includeHoursPadding
            ? totalHours.ToString("00", CultureInfo.InvariantCulture)
            : totalHours.ToString(CultureInfo.InvariantCulture);
        string milliseconds = value.Milliseconds.ToString(
            new string('0', millisecondDigits),
            CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{value.Minutes:00}:{value.Seconds:00}{separator}{milliseconds}");
    }
}

public sealed record SubtitleCue(
    Guid SegmentId,
    int SegmentIndex,
    double StartSeconds,
    double EndSeconds,
    string Text);

public sealed record AssSubtitleStyleOptions(
    int PlayResX = 1920,
    int PlayResY = 1080,
    int FontSize = 48,
    string FontName = "Arial",
    int MarginL = 80,
    int MarginR = 80,
    int MarginV = 60);
