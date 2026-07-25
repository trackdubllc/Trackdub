using Trackdub.Composition.Headless;

namespace Trackdub.Benchmarks;

/// <summary>
/// Orchestrates dubbing benchmark runs across multiple media files and/or
/// multiple target languages, producing one <see cref="DubbingBenchmarkReport"/>
/// per (file × language) combination.
/// </summary>
public sealed class DubbingBatchRunner(
    string? modelDirectory = null,
    string? ffmpegPath = null,
    string? ffprobePath = null)
{
    private readonly string? _modelDirectory = modelDirectory;
    private readonly string? _ffmpegPath = ffmpegPath;
    private readonly string? _ffprobePath = ffprobePath;

    /// <summary>
    /// Run the dubbing pipeline for every combination of <paramref name="mediaFiles"/>
    /// × <paramref name="targetLanguages"/> and return one report per run.
    /// Shares one headless host and one hardware snapshot across all runs.
    /// </summary>
    public async Task<IReadOnlyList<DubbingBenchmarkReport>> RunBatchAsync(
        IReadOnlyList<string> mediaFiles,
        IReadOnlyList<string> targetLanguages,
        string? sourceLanguageCode = null,
        string? outputRoot = null,
        bool forceRerun = false,
        CancellationToken cancellationToken = default)
    {
        char[] invalidChars = [.. Path.GetInvalidFileNameChars(), '/', '\\'];
        foreach (string language in targetLanguages)
        {
            if (language.IndexOfAny(invalidChars) >= 0)
            {
                throw new ArgumentException(
                    $"Target language code contains invalid path characters: {language}",
                    nameof(targetLanguages));
            }
        }

        var runner = new DubbingBenchmarkRunner(_modelDirectory, _ffmpegPath, _ffprobePath);
        var reports = new List<DubbingBenchmarkReport>(mediaFiles.Count * targetLanguages.Count);

        string hardwareInfo = BenchmarkHardwareInfo.Capture();

        HeadlessDubbingHost? host = null;
        string? hostCreationFailure = null;
        try
        {
            host = runner.CreateHost();
        }
        catch (Exception ex)
        {
            // Capture host creation failure for later reporting
            hostCreationFailure = ex.Message;
        }

        if (host is not null)
        {
            using (host)
            {
                foreach (string mediaFile in mediaFiles)
                {
                    foreach (string language in targetLanguages)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var options = new DubbingBenchmarkOptions(
                            InputPath: mediaFile,
                            TargetLanguage: language,
                            SourceLanguageCode: sourceLanguageCode,
                            OutputDirectory: outputRoot,
                            ForceRerun: forceRerun);

                        DubbingBenchmarkReport report = await runner.RunAsync(
                            host,
                            options,
                            hardwareInfo,
                            cancellationToken).ConfigureAwait(false);

                        reports.Add(report);
                    }
                }
            }
        }
        else
        {
            foreach (string mediaFile in mediaFiles)
            {
                foreach (string language in targetLanguages)
                {
                    var options = new DubbingBenchmarkOptions(
                        InputPath: mediaFile,
                        TargetLanguage: language,
                        SourceLanguageCode: sourceLanguageCode,
                        OutputDirectory: outputRoot,
                        ForceRerun: forceRerun);

                    DubbingBenchmarkReport report = DubbingBenchmarkRunner.CreateFailureReport(
                        options,
                        hardwareInfo,
                        hostCreationFailure ?? "Host initialization failed");

                    reports.Add(report);
                }
            }
        }

        return reports.AsReadOnly();
    }
}
