using Trackdub.Contracts;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Single source of truth for the export-gating subset of the execution snapshot.
/// The audio/subtitle/encoder flags recorded here gate the Export stage's artifact resume:
/// changing any value must invalidate a cached export so it reruns without --force-rerun.
/// Both the run-start execution snapshot (CaptureExecutionSnapshot) and the per-run
/// ExportManifest persist their gating values through this helper so capture and comparison
/// cannot drift.
/// </summary>
internal static class ExportResumeGating
{
    public const string ExportFormatKey = "ExportFormat";
    public const string ApplyTimbrePolishKey = "ApplyTimbrePolish";
    public const string RestoreOriginalPanKey = "RestoreOriginalPan";
    public const string MatchOriginalLoudnessKey = "MatchOriginalLoudness";
    public const string BurnInSubtitlesKey = "BurnInSubtitles";
    public const string SubtitleSourceKey = "SubtitleSource";
    public const string SubtitleFormatsKey = "SubtitleFormats";
    public const string VideoEncoderKey = "VideoEncoder";

    /// <summary>
    /// The canonical set of snapshot keys that gate an export resume. When any of these differ
    /// between the current run's snapshot and a prior run's persisted manifest, the cached
    /// export must not be resumed.
    /// </summary>
    public static IReadOnlyList<string> GatingKeys { get; } =
    [
        ExportFormatKey,
        ApplyTimbrePolishKey,
        RestoreOriginalPanKey,
        MatchOriginalLoudnessKey,
        BurnInSubtitlesKey,
        SubtitleSourceKey,
        SubtitleFormatsKey,
        VideoEncoderKey,
    ];

    /// <summary>
    /// Builds the canonical export-gating key/value pairs from the already-resolved export
    /// primitives. The <paramref name="subtitleFormatsToken"/> is produced by
    /// <see cref="SubtitleFormatsTokenFromRawOptions"/> on both the snapshot side (the run-start
    /// options) and the manifest side (the export request's raw requested formats), so both paths
    /// share the same normalization and agree for the default (null) case as well as for
    /// equivalent explicit requests.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        ExportOutputContainer container,
        bool applyTimbrePolish,
        bool restoreOriginalPan,
        bool matchOriginalLoudness,
        bool burnInSubtitles,
        ExportSubtitleSource subtitleSource,
        string subtitleFormatsToken,
        VideoEncoderPreference videoEncoder) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ExportFormatKey] = ContainerKey(container),
            [ApplyTimbrePolishKey] = applyTimbrePolish.ToString(),
            [RestoreOriginalPanKey] = restoreOriginalPan.ToString(),
            [MatchOriginalLoudnessKey] = matchOriginalLoudness.ToString(),
            [BurnInSubtitlesKey] = burnInSubtitles.ToString(),
            [SubtitleSourceKey] = subtitleSource.ToString(),
            [SubtitleFormatsKey] = subtitleFormatsToken,
            [VideoEncoderKey] = VideoEncoderPreferenceSettings.ToKey(videoEncoder),
        };

    /// <summary>
    /// Subtitle-formats token from the caller's RAW requested formats, used by BOTH the run-start
    /// execution snapshot and the per-run ExportManifest so the two sides always agree. Maps null
    /// (pipeline default SRT) to "default"; an empty list (all subtitles suppressed) to an empty
    /// token distinct from "default"; and other lists to their case-insensitively normalized
    /// ExportSubtitleFormat names. Order and duplicates are preserved to mirror what the export
    /// actually emits. Comparing raw-to-raw avoids the transcript-state dependency that resolving
    /// formats would introduce, and keeps the null / empty / explicit cases genuinely distinct.
    /// </summary>
    public static string SubtitleFormatsTokenFromRawOptions(IReadOnlyList<string>? formats) =>
        formats is null
            ? "default"
            : string.Join(",", NormalizeRawFormatTokens(formats));

    private static IReadOnlyList<string> NormalizeRawFormatTokens(IReadOnlyList<string> formats)
    {
        var result = new List<string>(formats.Count);
        foreach (string format in formats)
        {
            if (format.Equals("srt", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ExportSubtitleFormat.Srt.ToString());
            }
            else if (format.Equals("vtt", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ExportSubtitleFormat.Vtt.ToString());
            }
            else if (format.Equals("ass", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ExportSubtitleFormat.Ass.ToString());
            }
        }

        return result;
    }

    private static string ContainerKey(ExportOutputContainer container) =>
        container == ExportOutputContainer.Mkv ? "mkv" : "mp4";
}
