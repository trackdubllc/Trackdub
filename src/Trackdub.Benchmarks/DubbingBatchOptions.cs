namespace Trackdub.Benchmarks;

/// <summary>
/// Options for batch dubbing benchmark runs (multiple media files × languages).
/// </summary>
public sealed record DubbingBatchOptions(
    /// <summary>Directory containing media files to process.</summary>
    string VideosDirectory,
    /// <summary>BCP-47 target language codes (at least one required).</summary>
    IReadOnlyList<string> TargetLanguages,
    /// <summary>BCP-47 source language code (null = auto-detect).</summary>
    string? SourceLanguageCode = null,
    /// <summary>
    /// Root directory for reports and project subfolders
    /// (null = projects beside each media file; reports under ~/TrackdubBenchmarks).
    /// </summary>
    string? OutputDirectory = null,
    /// <summary>When true, re-execute all stages even if valid artifacts exist.</summary>
    bool ForceRerun = false,
    /// <summary>Show help and exit.</summary>
    bool ShowHelp = false)
{
    private static readonly string[] MediaExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm"];

    /// <summary>
    /// Parses CLI arguments for batch mode after the leading <c>--batch</c> token
    /// has been consumed by the caller.
    /// Usage: [videos-dir] --languages fr,de,it,ja [--source-language &lt;code&gt;]
    ///        [--output &lt;dir&gt;] [--force-rerun] [--help]
    /// </summary>
    public static bool TryParse(string[] args, TextWriter error, out DubbingBatchOptions? options)
    {
        options = null;

        if (args.Any(arg =>
                arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            options = new DubbingBatchOptions(
                VideosDirectory: string.Empty,
                TargetLanguages: [],
                ShowHelp: true);
            return true;
        }

        string? videosDir = null;
        var languages = new List<string>();
        string? sourceLanguageCode = null;
        string? outputDirectory = null;
        bool forceRerun = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--batch":
                    // Defensive: allow --batch <dir> if caller did not strip the flag.
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine("Error: --batch requires a videos directory.");
                        return false;
                    }

                    videosDir = args[++i];
                    break;

                case "--languages":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    {
                        error.WriteLine("Error: --languages requires a value.");
                        return false;
                    }

                    languages.AddRange(
                        args[++i]
                            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                default:
                    if (!DubbingBenchmarkOptions.TryParseCommonFlag(
                            args,
                            ref i,
                            error,
                            ref sourceLanguageCode,
                            ref outputDirectory,
                            ref forceRerun,
                            out bool matched))
                    {
                        return false;
                    }

                    if (matched)
                        break;

                    if (videosDir is null && !args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        videosDir = args[i];
                        break;
                    }

                    error.WriteLine($"Error: Unknown batch option '{args[i]}'.");
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(videosDir) || !Directory.Exists(videosDir))
        {
            error.WriteLine("Error: --batch requires an existing videos directory.");
            error.WriteLine("Usage: dubbing --batch <videos-dir> --languages fr,de,it,ja");
            return false;
        }

        if (languages.Count == 0)
        {
            error.WriteLine("Error: --languages is required for batch mode (comma-separated BCP-47 codes).");
            return false;
        }

        options = new DubbingBatchOptions(
            VideosDirectory: Path.GetFullPath(videosDir),
            TargetLanguages: languages.AsReadOnly(),
            SourceLanguageCode: sourceLanguageCode,
            OutputDirectory: outputDirectory,
            ForceRerun: forceRerun);
        return true;
    }

    /// <summary>
    /// Discovers media files under <see cref="VideosDirectory"/> using
    /// case-insensitive extension matching (Linux/macOS safe).
    /// </summary>
    public IReadOnlyList<string> DiscoverMediaFiles()
    {
        return Directory.EnumerateFiles(VideosDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }
}
